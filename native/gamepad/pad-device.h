// SPDX-License-Identifier: MIT
#pragma once
#include "pad-state.h"
#include <memory>
#include <string>
namespace xtr {
// User-mode adapters only. No custom driver, SDL interposition or xemu hooks.
class PadDevice {
public:
    PadDevice();
    ~PadDevice();
    PadDevice(const PadDevice&)=delete;
    PadDevice& operator=(const PadDevice&)=delete;
    void apply(const PadState& state);
    std::string path() const;
    static const char* backend();
    static unsigned supported_buttons();
private:
    struct Impl;
    std::unique_ptr<Impl> impl_;
};
}
