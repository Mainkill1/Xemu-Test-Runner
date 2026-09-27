#include "pad-state.h"
#include <cmath>
#include <iostream>
#include <stdexcept>
using namespace xtr;
static void require(bool v) { if (!v) throw std::runtime_error("Assertion failed"); }
int main() {
    auto p = parse_packet("state 7 12289 23 254 -32768 32767 12345 -5432");
    require(p.sequence == 7 && p.state.buttons == 12289 && p.state.lt == 23 && p.state.rt == 254);
    require(p.state.lx == -32768 && p.state.ly == 32767 && p.state.rx == 12345 && p.state.ry == -5432);
    require(wgi_buttons(0xf3ff) == 0x3fff); // Every supported bit, NOT a cast.
    require(wgi_buttons(0x1000) == 4 && wgi_buttons(0x10) == 1 && wgi_buttons(0x1) == 64);
    require(normalize_axis(-32768) == -1.0 && normalize_axis(32767) == 1.0 && normalize_axis(0) == 0.0);
    require(invert_axis(0) == 0 && invert_axis(-32768) == 32767 && invert_axis(32767) == -32768);
    for (const char* bad : {"state", "state 0 0 0 0 0 0 0 0", "state -1 0 0 0 0 0 0 0",
        "state 1 0 256 0 0 0 0 0", "state 1 0 0 0 -32769 0 0 0", "state 1 2048 0 0 0 0 0 0",
        "state 1 0 0 0 0 0 0 0 tail", "state 1 0 0 0 1.1 0 0 0"}) {
        bool threw=false; try { (void)parse_packet(bad); } catch (const std::exception&) { threw=true; }
        require(threw);
    }
    bool rejected=false; try { (void)wgi_buttons(0x400); } catch(const std::exception&) { rejected=true; }
    require(rejected);
    require(parse_packet("state 9007199254740991 0 0 0 0 0 0 0").sequence == 9007199254740991ULL);
    std::cout << "PASS full state parsing, bounds, button translation and exact axis endpoints\n";
}
