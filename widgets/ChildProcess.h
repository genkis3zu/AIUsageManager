#pragma once
#include <windows.h>
#include <string>
#include <vector>
#include <stdexcept>
#include <algorithm>

struct Handle
{
    HANDLE value=nullptr;
    explicit Handle(HANDLE handle=nullptr):value(handle){}
    ~Handle(){if(value&&value!=INVALID_HANDLE_VALUE)CloseHandle(value);}
    Handle(Handle const&)=delete;Handle& operator=(Handle const&)=delete;
};
inline void Require(bool condition){if(!condition)throw std::runtime_error("Widget worker unavailable ("+std::to_string(GetLastError())+")");}

inline std::string RunChild(std::wstring const& executable,std::wstring const& argument,DWORD timeout=30000,HANDLE cancel=nullptr)
{
    SECURITY_ATTRIBUTES security{sizeof(security),nullptr,TRUE};Handle read,write;
    Require(CreatePipe(&read.value,&write.value,&security,0)!=FALSE);
    Require(SetHandleInformation(read.value,HANDLE_FLAG_INHERIT,0)!=FALSE);
    Handle input(CreateFileW(L"NUL",GENERIC_READ,FILE_SHARE_READ|FILE_SHARE_WRITE,&security,OPEN_EXISTING,0,nullptr));
    Handle error(CreateFileW(L"NUL",GENERIC_WRITE,FILE_SHARE_READ|FILE_SHARE_WRITE,&security,OPEN_EXISTING,0,nullptr));
    Require(input.value!=INVALID_HANDLE_VALUE&&error.value!=INVALID_HANDLE_VALUE);
    SIZE_T bytes=0;InitializeProcThreadAttributeList(nullptr,1,0,&bytes);
    std::vector<unsigned char> storage(bytes);
    auto attributes=reinterpret_cast<PPROC_THREAD_ATTRIBUTE_LIST>(storage.data());
    Require(InitializeProcThreadAttributeList(attributes,1,0,&bytes)!=FALSE);
    struct Cleanup{PPROC_THREAD_ATTRIBUTE_LIST p;~Cleanup(){DeleteProcThreadAttributeList(p);}} cleanup{attributes};
    HANDLE inherited[]={input.value,write.value,error.value};
    Require(UpdateProcThreadAttribute(attributes,0,PROC_THREAD_ATTRIBUTE_HANDLE_LIST,inherited,sizeof(inherited),nullptr,nullptr)!=FALSE);
    Handle job(CreateJobObjectW(nullptr,nullptr));Require(job.value!=nullptr);
    JOBOBJECT_EXTENDED_LIMIT_INFORMATION limits{};limits.BasicLimitInformation.LimitFlags=JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE;
    Require(SetInformationJobObject(job.value,JobObjectExtendedLimitInformation,&limits,sizeof(limits))!=FALSE);
    STARTUPINFOEXW startup{};startup.StartupInfo.cb=sizeof(startup);startup.StartupInfo.dwFlags=STARTF_USESTDHANDLES|STARTF_USESHOWWINDOW;
    startup.StartupInfo.wShowWindow=SW_HIDE;startup.StartupInfo.hStdInput=input.value;startup.StartupInfo.hStdOutput=write.value;startup.StartupInfo.hStdError=error.value;startup.lpAttributeList=attributes;
    PROCESS_INFORMATION info{};std::wstring command=L"\""+executable+L"\" "+argument;
    Require(CreateProcessW(executable.c_str(),command.data(),nullptr,nullptr,TRUE,CREATE_NO_WINDOW|CREATE_SUSPENDED|EXTENDED_STARTUPINFO_PRESENT,nullptr,nullptr,&startup.StartupInfo,&info)!=FALSE);
    Handle process(info.hProcess),thread(info.hThread);
    if(!AssignProcessToJobObject(job.value,process.value)){TerminateProcess(process.value,1);throw std::runtime_error("Widget worker unavailable");}
    Require(ResumeThread(thread.value)!=static_cast<DWORD>(-1));CloseHandle(write.value);write.value=nullptr;
    uint64_t start=GetTickCount64();std::string output;
    for(;;)
    {
        if(GetTickCount64()-start>timeout||(cancel&&WaitForSingleObject(cancel,0)==WAIT_OBJECT_0))throw std::runtime_error("Widget worker timed out");
        DWORD available=0;
        if(!PeekNamedPipe(read.value,nullptr,0,nullptr,&available,nullptr)){Require(GetLastError()==ERROR_BROKEN_PIPE);break;}
        if(available) {
            char buffer[8192];DWORD count=0;
            Require(ReadFile(read.value,buffer,(std::min)(available,static_cast<DWORD>(sizeof(buffer))),&count,nullptr)!=FALSE);
            Require(output.size()+count<=262144);output.append(buffer,count);
        } else if(WaitForSingleObject(process.value,10)==WAIT_OBJECT_0) {
            // The child may have written its final bytes during the wait.
            DWORD remaining=0;
            if(PeekNamedPipe(read.value,nullptr,0,nullptr,&remaining,nullptr)&&remaining>0)continue;
            break;
        }
    }
    Require(WaitForSingleObject(process.value,1000)==WAIT_OBJECT_0);DWORD exitCode=1;
    Require(GetExitCodeProcess(process.value,&exitCode)!=FALSE&&exitCode==0&&!output.empty());return output;
}
