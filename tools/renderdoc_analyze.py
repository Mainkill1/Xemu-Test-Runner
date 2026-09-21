#!/usr/bin/env python3
"""Small machine-readable RenderDoc capture inventory."""
import argparse
import json
import os
import sys

try:
    import renderdoc as rd
except Exception as exc:
    print(f"cannot import renderdoc: {exc}", file=sys.stderr)
    raise SystemExit(20)

parser = argparse.ArgumentParser()
parser.add_argument("--capture", required=True)
parser.add_argument("--output", required=True)
args = parser.parse_args()


def action_count(actions):
    total = 0
    for action in actions:
        total += 1 + action_count(action.children)
    return total


rd.InitialiseReplay(rd.GlobalEnvironment(), [])
capture = rd.OpenCaptureFile()
controller = None
try:
    opened = capture.OpenFile(args.capture, "", None)
    if opened != rd.ResultCode.Succeeded:
        raise RuntimeError(f"OpenFile failed: {opened}")
    if not capture.LocalReplaySupport():
        raise RuntimeError("capture has no local replay support")
    opened, controller = capture.OpenCapture(rd.ReplayOptions(), None)
    if opened != rd.ResultCode.Succeeded:
        raise RuntimeError(f"OpenCapture failed: {opened}")

    textures = []
    for tex in controller.GetTextures():
        textures.append({
            "resourceId": str(tex.resourceId),
            "name": tex.name,
            "width": int(tex.width),
            "height": int(tex.height),
            "depth": int(tex.depth),
            "mips": int(tex.mips),
            "arraysize": int(tex.arraysize),
            "format": str(tex.format),
            "type": str(tex.type),
        })

    buffers = []
    for buf in controller.GetBuffers():
        buffers.append({
            "resourceId": str(buf.resourceId),
            "name": buf.name,
            "length": int(buf.length),
        })

    roots = controller.GetRootActions()
    result = {
        "capture": os.path.abspath(args.capture),
        "driver": str(capture.DriverName()),
        "actionCount": action_count(roots),
        "rootActionCount": len(roots),
        "textureCount": len(textures),
        "bufferCount": len(buffers),
        "textures": textures,
        "buffers": buffers,
    }
    os.makedirs(os.path.dirname(os.path.abspath(args.output)), exist_ok=True)
    with open(args.output, "w", encoding="utf-8") as handle:
        json.dump(result, handle, indent=2)
        handle.write("\n")
finally:
    if controller is not None:
        controller.Shutdown()
    capture.Shutdown()
    rd.ShutdownReplay()
