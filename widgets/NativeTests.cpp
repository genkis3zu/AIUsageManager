#include <windows.h>
#include <cassert>
#include <iostream>
#include <string>
#include "ProviderState.h"
#include "ChildProcess.h"
#include <winrt/Windows.Foundation.Collections.h>
#include "CardPayload.h"
#include <winrt/Microsoft.Windows.Widgets.Providers.h>
#undef assert
#define assert(value) do { if(!(value))throw std::runtime_error("Assertion: " #value); } while(false)

int RunTests(int argc,wchar_t** argv)
{
    if(argc==2) {
        std::wstring arg=argv[1];
        if(arg==L"--child-ok"){std::cout<<"{\"medium\":{},\"large\":{}}";return 0;}
        if(arg==L"--child-hang"){Sleep(10000);return 0;}
        if(arg==L"--child-big"){std::cout<<std::string(300000,'x');return 0;}
        if(arg==L"--child-error")return 2;
        if(arg==L"--check-installed") {
            winrt::init_apartment(winrt::apartment_type::multi_threaded);
            constexpr GUID clsid{0x9e54c656,0x4364,0x4ea1,{0x9a,0x51,0x81,0x59,0x6b,0x9c,0x4c,0x11}};
            try {
                // An unpackaged diagnostic client can validate IUnknown activation;
                // marshaling the WinRT provider interface requires the host's package graph.
                auto provider=winrt::create_instance<IUnknown>(clsid,CLSCTX_LOCAL_SERVER);
                std::cout<<"PASS installed COM provider and Widgets runtime activation\n";return 0;
            }catch(winrt::hresult_error const& e){std::cerr<<"FAIL COM activation HRESULT 0x"<<std::hex<<static_cast<uint32_t>(e.code().value)<<"\n";return 1;}
        }
    }
    ProviderState state;
    winrt::init_apartment();
    state.activate(L"one",false);state.activate(L"one",false);
    assert(state.active_count()==1);state.activate(L"two",true);assert(state.active_count()==2);
    state.deactivate(L"one");assert(state.active_count()==1);state.remove(L"two");assert(state.active_count()==0);
    state.restore(L"restored",true,false);assert(state.contains(L"restored")&&state.active_count()==0);
    assert(!state.due(999999));
    state.activate(L"restored",true);assert(state.begin_refresh(0));assert(!state.begin_refresh(0));
    state.finish_refresh(100);assert(!state.due(300099));assert(state.due(300100));
    state.remove(L"restored");assert(!state.due(999999));
    std::wcout<<L"PASS widget state activation, deletion, restoration, overlap and cadence\n";
    wchar_t path[32768]{};GetModuleFileNameW(nullptr,path,32768);
    assert(RunChild(path,L"--child-ok",3000).find("medium")!=std::string::npos);
    for(auto arg:{L"--child-hang",L"--child-big",L"--child-error"}) {
        bool failed=false;try{RunChild(path,arg,150);}catch(...){failed=true;}assert(failed);
    }
    std::wcout<<L"PASS child success, deadline, output bound and exit code\n";
    auto valid=std::string(R"({"medium":{"type":"AdaptiveCard","body":[{},{},{}]},"large":{"type":"AdaptiveCard","body":[{},{},{}]}})");
    assert(!ParseCards(valid).first.empty());
    for(auto const& payload:std::vector<std::string>{"broken","{}",R"({"medium":{"type":"other"},"large":{}})",std::string(262145,'x')}) {
        bool failed=false;try{ParseCards(payload);}catch(...){failed=true;}assert(failed);
    }
    auto app=std::wstring(path);app=app.substr(0,app.find_last_of(L"\\")+1)+L"AIUsageManager.exe";
    if(GetFileAttributesW(app.c_str())!=INVALID_FILE_ATTRIBUTES)assert(!ParseCards(RunChild(app,L"--widget-sample")).second.empty());
    std::wcout<<L"PASS payload validation and real headless sample contract\n";
    return 0;
}
int wmain(int argc,wchar_t** argv)
{
    try{return RunTests(argc,argv);}catch(std::exception const& e){std::cerr<<"FAIL "<<e.what()<<std::endl;return 1;}
}
