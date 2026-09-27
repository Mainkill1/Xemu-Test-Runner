// SPDX-License-Identifier: MIT
#include "pad-device.h"
#include <chrono>
#include <condition_variable>
#include <exception>
#include <iostream>
#include <mutex>
#include <optional>
#include <thread>
#ifdef _WIN32
#include <windows.h>
#include <winrt/base.h>
#else
#include <poll.h>
#include <unistd.h>
#include <csignal>
#endif
using Clock=std::chrono::steady_clock;
using namespace std::chrono_literals;
namespace {
// The OS device belongs to this thread (including WinRT apartment lifetime).
// Its independent deadline still neutralizes/destroys input if stdout is blocked.
class Session {
    std::mutex mutex_;
    std::condition_variable changed_;
    std::thread thread_;
    std::optional<xtr::PadPacket> pending_;
    std::exception_ptr failure_;
    std::string path_;
    bool initialized_=false, stop_=false;
    std::uint64_t applied_=0;
    std::int64_t applied_us_=0;
    void run() {
        try {
            xtr::PadDevice device;
            std::unique_lock<std::mutex> lock(mutex_);
            path_=device.path(); initialized_=true; changed_.notify_all();
            auto deadline=Clock::time_point::max();
            while (!stop_) {
                if (!changed_.wait_until(lock,deadline,[&] {return stop_ || pending_.has_value();}))
                    throw std::runtime_error("Controller heartbeat expired; device neutralized and removed");
                if (stop_) break;
                // A queued request must not resurrect a session after its deadline.
                if (Clock::now()>=deadline) throw std::runtime_error("Controller deadline expired");
                const auto packet=*pending_;
                lock.unlock(); device.apply(packet.state); lock.lock();
                applied_=packet.sequence;
                applied_us_=std::chrono::duration_cast<std::chrono::microseconds>(Clock::now().time_since_epoch()).count();
                pending_.reset(); deadline=Clock::now()+250ms; changed_.notify_all();
            }
        } catch (...) {
            std::lock_guard<std::mutex> lock(mutex_); failure_=std::current_exception(); initialized_=true; changed_.notify_all();
        }
    }
public:
    Session() { thread_=std::thread([this] {run();}); }
    ~Session() { {std::lock_guard<std::mutex> lock(mutex_); stop_=true; changed_.notify_all();} thread_.join(); }
    void ready() { std::unique_lock<std::mutex> lock(mutex_); changed_.wait(lock,[&] {return initialized_;}); if(failure_)std::rethrow_exception(failure_); }
    std::string path() {std::lock_guard<std::mutex> lock(mutex_);return path_;}
    void healthy() {std::lock_guard<std::mutex> lock(mutex_); if(failure_)std::rethrow_exception(failure_);}
    std::int64_t apply(xtr::PadPacket packet) {
        std::unique_lock<std::mutex> lock(mutex_);
        if (failure_) std::rethrow_exception(failure_);
        if(packet.sequence<=applied_) throw std::invalid_argument("Stale/duplicate controller sequence");
        pending_=packet; changed_.notify_all();
        changed_.wait(lock,[&] {return failure_ || !pending_;});
        if (failure_) std::rethrow_exception(failure_);
        return applied_us_;
    }
};
// Return -1 for EOF, 0 for no data, 1 for one byte. Never wait on a whole line.
int input(char& c) {
#ifdef _WIN32
    HANDLE handle=GetStdHandle(STD_INPUT_HANDLE); DWORD available=0,read=0;
    if(GetFileType(handle)!=FILE_TYPE_PIPE) throw std::runtime_error("Use a supervisor-owned stdin pipe");
    if(!PeekNamedPipe(handle,nullptr,0,nullptr,&available,nullptr)) {
        if(GetLastError()==ERROR_BROKEN_PIPE) return -1;
        throw std::runtime_error("Input pipe status failed");
    }
    if(!available) {std::this_thread::sleep_for(2ms);return 0;}
    if(!ReadFile(handle,&c,1,&read,nullptr)||!read) return -1;
#else
    pollfd fd{STDIN_FILENO,POLLIN,0};
    int result=poll(&fd,1,2);
    if(result<0) {if(errno==EINTR)return 0;throw std::runtime_error("Input pipe polling failed");}
    if(!result) return 0;
    auto count=read(STDIN_FILENO,&c,1);
    if(count<0) {if(errno==EINTR)return 0;throw std::runtime_error("Input pipe read failed");}
    if(!count)return -1;
#endif
    return 1;
}
}
int main(int argc,char** argv) {
#ifndef _WIN32
    std::signal(SIGPIPE,SIG_IGN);
#endif
    if(argc!=1) {std::cerr<<"No options accepted; one standard gamepad per worker.\n";return 2;}
    (void)argv;
    try {
        Session session; session.ready();
        std::cout<<"{\"type\":\"ready\",\"protocolVersion\":1,\"backend\":\""<<xtr::PadDevice::backend()
            <<"\",\"supportedButtons\":"<<xtr::PadDevice::supported_buttons()
            <<",\"watchdogMs\":250,\"systemWide\":true,\"additionalDriver\":false,\"deviceSysname\":\""<<session.path()<<"\"}"<<std::endl;
        std::string line; char c=0;
        while(true) {
            session.healthy(); if(!std::cout) throw std::runtime_error("Supervisor output pipe failed");
            int result=input(c); if(result==0)continue;
            if(result<0) {if(!line.empty())throw std::runtime_error("Partial input command at EOF");break;}
            if(c=='\n') {
                if(!line.empty()&&line.back()=='\r')line.pop_back();
                if(line=="stop")break;
                auto packet=xtr::parse_packet(line);
                auto timestamp=session.apply(packet);
                std::cout<<"{\"type\":\"applied\",\"sequence\":"<<packet.sequence<<",\"appliedAtUs\":"<<timestamp<<"}"<<std::endl;
                line.clear();
            } else {
                if(c=='\0'||line.size()>=256)throw std::runtime_error("Oversized/invalid controller command");
                line.push_back(c);
            }
        }
        // Session destruction neutralizes and removes the OS device before returning.
        return 0;
    } catch(const std::exception& e) {std::cerr<<"Gamepad unavailable or failed: "<<e.what()<<'\n';return 3;}
#ifdef _WIN32
    catch(const winrt::hresult_error& e) { std::cerr<<"WinRT gamepad failed, HRESULT="<<std::hex<<static_cast<unsigned>(e.code().value)<<'\n'; return 3; }
#endif
    catch(...) {std::cerr<<"OS gamepad operation failed\n";return 3;}
}
