#define WIN32_LEAN_AND_MEAN
#define NOMINMAX
#include <windows.h>
#include <objbase.h>
#include <cstdint>

static HANDLE shutdownEvent=nullptr;
namespace winrt {
    inline auto get_module_lock() noexcept {
        struct Lock {
            uint32_t operator++() noexcept{return CoAddRefServerProcess();}
            uint32_t operator--() noexcept{auto count=CoReleaseServerProcess();if(count==0&&shutdownEvent)SetEvent(shutdownEvent);return count;}
        };return Lock{};
    }
}
#define WINRT_CUSTOM_MODULE_LOCK
#include <winrt/Windows.Foundation.h>
#include <winrt/Windows.Foundation.Collections.h>
#include <winrt/Microsoft.Windows.Widgets.Providers.h>
#include <winrt/Microsoft.Windows.Widgets.h>
#include <mutex>
#include <thread>
#include <condition_variable>
#include <chrono>
#include "ProviderState.h"
#include "ChildProcess.h"
#include "CardPayload.h"

namespace W=winrt::Microsoft::Windows::Widgets;
namespace P=winrt::Microsoft::Windows::Widgets::Providers;
static constexpr GUID ProviderClsid{0x9e54c656,0x4364,0x4ea1,{0x9a,0x51,0x81,0x59,0x6b,0x9c,0x4c,0x11}};

static std::wstring AppPath()
{
    wchar_t path[32768]{};DWORD length=GetModuleFileNameW(nullptr,path,32768);Require(length>0&&length<32768);
    std::wstring directory(path,length);return directory.substr(0,directory.find_last_of(L"\\")+1)+L"AIUsageManager.exe";
}
static void OpenTray(std::wstring const& argument=L"--show")
{
    auto path=AppPath();std::wstring command=L"\""+path+L"\" "+argument;
    STARTUPINFOW startup{sizeof(startup)};PROCESS_INFORMATION process{};
    if(CreateProcessW(path.c_str(),command.data(),nullptr,nullptr,FALSE,0,nullptr,nullptr,&startup,&process)){CloseHandle(process.hThread);CloseHandle(process.hProcess);}
}
static winrt::hstring UnavailableCard()
{
    return LR"({"type":"AdaptiveCard","version":"1.5","body":[{"type":"TextBlock","text":"使用量は未取得です","weight":"bolder"},{"type":"TextBlock","text":"更新するか、アプリでログイン状態を確認してください。","wrap":true}],"actions":[{"type":"Action.Execute","title":"更新","verb":"refresh"},{"type":"Action.Execute","title":"アプリを開く","verb":"open"}]})";
}

struct Provider:winrt::implements<Provider,P::IWidgetProvider>
{
    std::mutex gate;std::condition_variable changed;ProviderState state;
    Handle cancel{CreateEventW(nullptr,TRUE,FALSE,nullptr)};std::thread worker;bool stopping=false;
    Provider()
    {
        Require(cancel.value!=nullptr);
        // Fail COM activation if the package cannot resolve the Windows Widgets runtime.
        auto manager=P::WidgetManager::GetDefault();
        try {
            for(auto const& info:manager.GetWidgetInfos()) {
                auto context=info.WidgetContext();
                if(context.DefinitionId()==L"AIUsage")state.restore(std::wstring(context.Id()),context.Size()==W::WidgetSize::Large,context.IsActive());
            }
        }catch(...){}
        worker=std::thread([this]{Run();});
    }
    ~Provider()
    {
        {std::lock_guard lock(gate);stopping=true;SetEvent(cancel.value);}changed.notify_all();if(worker.joinable())worker.join();
    }
    void Queue(P::WidgetContext const& context,bool activating=false)
    {
        if(context.DefinitionId()!=L"AIUsage")return;
        std::wstring id(context.Id());bool large=context.Size()==W::WidgetSize::Large;
        {std::lock_guard lock(gate);state.restore(id,large,activating||context.IsActive());}changed.notify_one();
    }
    void CreateWidget(P::WidgetContext const& context){Queue(context);}
    void DeleteWidget(winrt::hstring const& id,winrt::hstring const&){std::lock_guard lock(gate);state.remove(std::wstring(id));}
    void OnActionInvoked(P::WidgetActionInvokedArgs const& args)
    {
        if(args.Verb()==L"refresh")Queue(args.WidgetContext());
        else if(args.Verb()==L"open")OpenTray();
        else if(args.Verb()==L"login-codex")OpenTray(L"--login-codex");
        else if(args.Verb()==L"login-grok")OpenTray(L"--login-grok");
    }
    void OnWidgetContextChanged(P::WidgetContextChangedArgs const& args){Queue(args.WidgetContext());}
    void Activate(P::WidgetContext const& context){Queue(context,true);}
    void Deactivate(winrt::hstring const& id){std::lock_guard lock(gate);state.deactivate(std::wstring(id));}
    void Run() noexcept
    {
        try {
            winrt::init_apartment(winrt::apartment_type::multi_threaded);
            std::unique_lock lock(gate);
            while(!stopping) {
                changed.wait_for(lock,std::chrono::seconds(1),[this]{return stopping||state.due(GetTickCount64());});
                if(stopping)break;if(!state.begin_refresh(GetTickCount64()))continue;
                lock.unlock();
                auto cards=std::make_pair(UnavailableCard(),UnavailableCard());
                try{cards=ParseCards(RunChild(AppPath(),L"--widget-data",30000,cancel.value));}catch(...){}
                lock.lock();if(stopping)break;
                auto snapshot=state.snapshot();
                lock.unlock();
                // Retain only copied IDs and size values; never host callback objects.
                for(auto const& [id,slot]:snapshot) {
                    if(!slot.active)continue;
                    {std::lock_guard check(gate);if(stopping||!state.contains(id))continue;}
                    try {
                        P::WidgetUpdateRequestOptions options{winrt::hstring(id)};
                        options.Template(slot.large?cards.second:cards.first);options.Data(L"{}");
                        P::WidgetManager::GetDefault().UpdateWidget(options);
                    }catch(...){}
                }
                lock.lock();
                state.finish_refresh(GetTickCount64());
            }
        }catch(...){}
    }
};

struct Factory:winrt::implements<Factory,IClassFactory,winrt::no_module_lock>
{
    std::mutex gate;winrt::weak_ref<Provider> instance;
    HRESULT __stdcall CreateInstance(IUnknown* outer,REFIID iid,void** result) noexcept override
    {
        if(!result)return E_POINTER;*result=nullptr;if(outer)return CLASS_E_NOAGGREGATION;
        try{std::lock_guard lock(gate);auto current=instance.get();if(!current){current=winrt::make_self<Provider>();instance=current->get_weak();}return current.as(iid,result);}catch(...){return winrt::to_hresult();}
    }
    HRESULT __stdcall LockServer(BOOL) noexcept override{return S_OK;}
};

int WINAPI wWinMain(HINSTANCE,HINSTANCE,PWSTR command,int)
{
    try {
        winrt::init_apartment(winrt::apartment_type::multi_threaded);
        if(std::wstring(command)==L"--check-registration") {
            auto infos=P::WidgetManager::GetDefault().GetWidgetInfos();(void)infos;return 0;
        }
        Handle shutdown{CreateEventW(nullptr,TRUE,FALSE,nullptr)};Require(shutdown.value!=nullptr);shutdownEvent=shutdown.value;
        auto factory=winrt::make_self<Factory>();DWORD cookie=0;
        winrt::check_hresult(CoRegisterClassObject(ProviderClsid,factory.get(),CLSCTX_LOCAL_SERVER,REGCLS_MULTIPLEUSE,&cookie));
        WaitForSingleObject(shutdown.value,INFINITE);CoRevokeClassObject(cookie);shutdownEvent=nullptr;return 0;
    }catch(...){return 1;}
}
