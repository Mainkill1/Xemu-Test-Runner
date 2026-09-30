// SPDX-License-Identifier: MIT
#pragma once
#include <array>
#include <charconv>
#include <cstdint>
#include <stdexcept>
#include <string_view>
namespace xtr {
struct PadState { std::uint16_t buttons{}; std::uint8_t lt{}, rt{}; std::int16_t lx{}, ly{}, rx{}, ry{}; };
struct PadPacket { std::uint64_t sequence{}; PadState state{}; };
inline PadPacket parse_packet(std::string_view text) {
    if (text.size()>256 || text.substr(0,6)!="state ") throw std::invalid_argument("Invalid state command");
    text.remove_prefix(6);
    std::array<std::int64_t,8> n{};
    for (auto& value : n) {
        while (!text.empty() && text.front()==' ') text.remove_prefix(1);
        auto result=std::from_chars(text.data(),text.data()+text.size(),value);
        if (result.ec!=std::errc() || result.ptr==text.data()) throw std::invalid_argument("Invalid integer");
        text.remove_prefix(static_cast<std::size_t>(result.ptr-text.data()));
        if (!text.empty() && text.front()!=' ') throw std::invalid_argument("Invalid separator");
    }
    while (!text.empty() && text.front()==' ') text.remove_prefix(1);
    if (!text.empty() || n[0]<=0 || n[0]>9007199254740991LL || n[1]<0 || n[1]>65535 || (n[1]&0x800))
        throw std::invalid_argument("Invalid sequence or button mask");
    if (n[2]<0 || n[2]>255 || n[3]<0 || n[3]>255) throw std::invalid_argument("Trigger outside 0..255");
    for (std::size_t i=4;i<8;++i) if (n[i]<-32768 || n[i]>32767) throw std::invalid_argument("Stick outside int16 range");
    if ((n[1]&3)==3 || (n[1]&12)==12) throw std::invalid_argument("Opposing D-pad directions");
    return {static_cast<std::uint64_t>(n[0]), {static_cast<std::uint16_t>(n[1]),
        static_cast<std::uint8_t>(n[2]),static_cast<std::uint8_t>(n[3]),
        static_cast<std::int16_t>(n[4]),static_cast<std::int16_t>(n[5]),
        static_cast<std::int16_t>(n[6]),static_cast<std::int16_t>(n[7])}};
}
inline double normalize_axis(std::int16_t value) { return value<0 ? value/32768.0 : value/32767.0; }
inline std::int16_t invert_axis(std::int16_t value) {
    if (value==-32768) return 32767;
    if (value==32767) return -32768;
    return static_cast<std::int16_t>(-value);
}
inline std::uint32_t wgi_buttons(std::uint16_t mask) {
    // GamepadButtons is not the XInput bit layout. Guide has no equivalent.
    if ((mask & 0x0c00)!=0) throw std::invalid_argument("Windows InputInjector does not support Guide/reserved buttons");
    constexpr std::array<std::uint16_t,14> from={0x10,0x20,0x1000,0x2000,0x4000,0x8000,1,2,4,8,0x100,0x200,0x40,0x80};
    std::uint32_t result=0;
    for (std::size_t i=0;i<from.size();++i) if (mask&from[i]) result|=(1U<<i);
    return result;
}
}
