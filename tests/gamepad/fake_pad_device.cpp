// Protocol/timeout fixture only. No OS-visible input device is created.
#include "pad-device.h"
namespace xtr {
struct PadDevice::Impl {};
PadDevice::PadDevice() : impl_(std::make_unique<Impl>()) {}
PadDevice::~PadDevice() = default;
void PadDevice::apply(const PadState&) {}
std::string PadDevice::path() const { return "fixture"; }
const char* PadDevice::backend() { return "fixture-no-os-device"; }
unsigned PadDevice::supported_buttons() { return 0xf3ff; }
}
