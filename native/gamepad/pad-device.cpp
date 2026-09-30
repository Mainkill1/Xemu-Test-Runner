// SPDX-License-Identifier: MIT
#include "pad-device.h"
#include <stdexcept>
#ifdef _WIN32
#include <winrt/base.h>
#include <winrt/Windows.Gaming.Input.h>
#include <winrt/Windows.UI.Input.Preview.Injection.h>
namespace xtr {
using namespace winrt::Windows::UI::Input::Preview::Injection;
struct PadDevice::Impl {
    InputInjector injector{nullptr};
    Impl() {
        winrt::init_apartment(winrt::apartment_type::multi_threaded);
        try {
            injector=InputInjector::TryCreate();
            if (!injector) throw std::runtime_error("InputInjector access denied; requires a qualified capability/elevation context");
            injector.InitializeGamepadInjection();
        } catch (...) { injector=nullptr; winrt::uninit_apartment(); throw; }
    }
    ~Impl() {
        if (injector) { try { injector.UninitializeGamepadInjection(); } catch (...) {} }
        injector=nullptr;
        winrt::uninit_apartment();
    }
};
const char* PadDevice::backend() { return "windows-input-injector"; }
unsigned PadDevice::supported_buttons() { return 0xf3ff; }
std::string PadDevice::path() const { return {}; }
void PadDevice::apply(const PadState& s) {
    InjectedInputGamepadInfo info;
    info.Buttons(static_cast<winrt::Windows::Gaming::Input::GamepadButtons>(wgi_buttons(s.buttons)));
    info.LeftTrigger(s.lt/255.0); info.RightTrigger(s.rt/255.0);
    info.LeftThumbstickX(normalize_axis(s.lx)); info.LeftThumbstickY(normalize_axis(s.ly));
    info.RightThumbstickX(normalize_axis(s.rx)); info.RightThumbstickY(normalize_axis(s.ry));
    impl_->injector.InjectGamepadInput(info);
}
}
#else
#include <linux/uinput.h>
#include <sys/ioctl.h>
#include <fcntl.h>
#include <unistd.h>
#include <cerrno>
#include <cstring>
#include <filesystem>
namespace xtr {
namespace {
constexpr std::array<unsigned,11> keys={BTN_SOUTH,BTN_EAST,BTN_WEST,BTN_NORTH,
    BTN_TL,BTN_TR,BTN_SELECT,BTN_START,BTN_THUMBL,BTN_THUMBR,BTN_MODE};
constexpr std::array<unsigned,11> masks={0x1000,0x2000,0x4000,0x8000,0x100,0x200,0x20,0x10,0x40,0x80,0x400};
void check(int result,const char* action) { if (result<0) throw std::runtime_error(std::string(action)+": "+std::strerror(errno)); }
}
struct PadDevice::Impl {
    int fd=-1;
    std::string sysname;
    Impl() {
        fd=open("/dev/uinput",O_WRONLY|O_NONBLOCK|O_CLOEXEC);
        check(fd,"Opening existing /dev/uinput (operator provisioning required)");
        try {
            check(ioctl(fd,UI_SET_EVBIT,EV_KEY),"EV_KEY");
            for (auto code:keys) check(ioctl(fd,UI_SET_KEYBIT,code),"UI_SET_KEYBIT");
            check(ioctl(fd,UI_SET_EVBIT,EV_ABS),"EV_ABS");
            for (unsigned code:{ABS_X,ABS_Y,ABS_RX,ABS_RY,ABS_Z,ABS_RZ,ABS_HAT0X,ABS_HAT0Y}) {
                check(ioctl(fd,UI_SET_ABSBIT,code),"UI_SET_ABSBIT");
                uinput_abs_setup a{}; a.code=static_cast<__u16>(code);
                bool trigger=code==ABS_Z || code==ABS_RZ, hat=code==ABS_HAT0X || code==ABS_HAT0Y;
                a.absinfo.minimum=trigger?0:(hat?-1:-32768);
                a.absinfo.maximum=trigger?255:(hat?1:32767);
                check(ioctl(fd,UI_ABS_SETUP,&a),"UI_ABS_SETUP");
            }
            uinput_setup setup{};
            // Explicit virtual identity, not a claim to be a physical Microsoft device.
            setup.id.bustype=BUS_VIRTUAL; setup.id.version=1;
            std::strcpy(setup.name,"Xemu Runner Gamepad");
            check(ioctl(fd,UI_DEV_SETUP,&setup),"UI_DEV_SETUP");
            check(ioctl(fd,UI_DEV_CREATE),"UI_DEV_CREATE");
            char name[128]{};
            check(ioctl(fd,UI_GET_SYSNAME(sizeof(name)),name),"UI_GET_SYSNAME");
            sysname=name;
        } catch (...) { ioctl(fd,UI_DEV_DESTROY); close(fd); fd=-1; throw; }
    }
    ~Impl() { if (fd>=0) { ioctl(fd,UI_DEV_DESTROY); close(fd); } }
};
const char* PadDevice::backend() { return "linux-uinput"; }
unsigned PadDevice::supported_buttons() { return 0xf7ff; }
std::string PadDevice::path() const { return impl_->sysname; }
void PadDevice::apply(const PadState& s) {
    std::array<input_event,20> events{}; std::size_t count=0;
    auto add=[&](unsigned type,unsigned code,int value) {
        auto& e=events[count++]; e.type=static_cast<__u16>(type); e.code=static_cast<__u16>(code); e.value=value;
    };
    for (std::size_t i=0;i<keys.size();++i) add(EV_KEY,keys[i],(s.buttons&masks[i])!=0);
    add(EV_ABS,ABS_X,s.lx); add(EV_ABS,ABS_Y,invert_axis(s.ly));
    add(EV_ABS,ABS_RX,s.rx); add(EV_ABS,ABS_RY,invert_axis(s.ry));
    add(EV_ABS,ABS_Z,s.lt); add(EV_ABS,ABS_RZ,s.rt);
    add(EV_ABS,ABS_HAT0X,((s.buttons&8)!=0)-((s.buttons&4)!=0));
    add(EV_ABS,ABS_HAT0Y,((s.buttons&2)!=0)-((s.buttons&1)!=0));
    add(EV_SYN,SYN_REPORT,0);
    // One full report, terminated by SYN_REPORT. Never acknowledge a partial report.
    ssize_t written;
    do { written=write(impl_->fd,events.data(),count*sizeof(input_event)); } while (written<0 && errno==EINTR);
    if (written!=static_cast<ssize_t>(count*sizeof(input_event)))
        throw std::runtime_error("uinput report write failed or was incomplete");
}
}
#endif
namespace xtr {
PadDevice::PadDevice() : impl_(std::make_unique<Impl>()) { apply({}); }
PadDevice::~PadDevice() { try { apply({}); } catch (...) {} }
}
