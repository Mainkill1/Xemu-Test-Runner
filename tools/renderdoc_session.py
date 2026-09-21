#!/usr/bin/env python3
"""RenderDoc launch/target-control bridge for Xemu Test Runner.

Protocol: newline-delimited JSON on stdout/stdin. stdout is reserved for protocol
messages. Human diagnostics go to stderr.
"""
import argparse
import json
import os
import queue
import shlex
import shutil
import subprocess
import sys
import threading
import time

try:
    import renderdoc as rd
except Exception as exc:
    print(json.dumps({"type": "fatal", "error": f"cannot import renderdoc: {exc}"}), flush=True)
    raise SystemExit(20)


def emit(kind, **values):
    values["type"] = kind
    print(json.dumps(values, separators=(",", ":")), flush=True)


def command_line(arguments):
    if os.name == "nt":
        return subprocess.list2cmdline(arguments)
    return " ".join(shlex.quote(x) for x in arguments)


def result_succeeded(value):
    try:
        return value == rd.ResultCode.Succeeded
    except Exception:
        return "Succeeded" in str(value)


def output_paths(output, frames):
    output = os.path.abspath(output)
    if frames == 1:
        return [output]
    root, ext = os.path.splitext(output)
    if not ext:
        ext = ".rdc"
    return [f"{root}-{index:03d}{ext}" for index in range(1, frames + 1)]


parser = argparse.ArgumentParser()
parser.add_argument("--exe", required=True)
parser.add_argument("--cwd", required=True)
parser.add_argument("--capture-template", required=True)
parser.add_argument("program_args", nargs=argparse.REMAINDER)
args = parser.parse_args()
program_args = args.program_args
if program_args and program_args[0] == "--":
    program_args = program_args[1:]

rd.InitialiseReplay(rd.GlobalEnvironment(), [])
target = None
try:
    options = rd.CaptureOptions()
    env = []
    cmdline = command_line(program_args)
    try:
        launch = rd.ExecuteAndInject(args.exe, args.cwd, cmdline, env, args.capture_template, options, False)
    except TypeError:
        launch = rd.ExecuteAndInject(args.exe, args.cwd, cmdline, env, args.capture_template, options)

    status = getattr(launch, "result", getattr(launch, "status", None))
    if not result_succeeded(status):
        emit("fatal", error=f"ExecuteAndInject failed: {status}")
        raise SystemExit(21)

    ident = int(launch.ident)
    target = rd.CreateTargetControl(None, ident, "xemu-test-runner", True)
    if target is None or not target.Connected():
        emit("fatal", error=f"target control connection failed for ident {ident}")
        raise SystemExit(22)

    pid = int(target.GetPID())
    emit("ready", ident=ident, pid=pid, api=str(target.GetAPI()), target=str(target.GetTarget()))

    commands = queue.Queue()
    stop_input = threading.Event()

    def read_commands():
        while not stop_input.is_set():
            line = sys.stdin.readline()
            if line == "":
                commands.put({"command": "quit"})
                return
            try:
                commands.put(json.loads(line))
            except Exception as exc:
                emit("error", error=f"invalid command JSON: {exc}")

    threading.Thread(target=read_commands, daemon=True).start()
    pending = queue.Queue()
    quitting = False

    while not quitting and target.Connected():
        try:
            while True:
                command = commands.get_nowait()
                name = str(command.get("command", "")).lower()
                if name == "quit":
                    quitting = True
                    break
                if name not in ("capture", "wait_capture"):
                    emit("error", error=f"unsupported command: {name}")
                    continue
                frames = int(command.get("frames", 1))
                if frames < 1 or frames > 120:
                    emit("error", error="frames must be between 1 and 120")
                    continue
                if not pending.empty():
                    emit("error", error="a previous capture request is still pending")
                    continue
                outputs = output_paths(command["output"], frames)
                for output in outputs:
                    os.makedirs(os.path.dirname(output), exist_ok=True)
                    pending.put(output)
                if name == "capture":
                    target.TriggerCapture(frames)
                emit("armed", command=name, expected=frames, outputs=outputs)
        except queue.Empty:
            pass

        if quitting:
            break

        try:
            message = target.ReceiveMessage(None)
        except Exception as exc:
            emit("fatal", error=f"ReceiveMessage failed: {exc}")
            break

        if message is not None and message.type == rd.TargetControlMessageType.NewCapture:
            source = message.newCapture.path
            capture_id = int(message.newCapture.captureId)
            try:
                output = pending.get_nowait()
            except queue.Empty:
                output = os.path.abspath(f"{args.capture_template}-{capture_id}.rdc")
            try:
                shutil.copy2(source, output)
                emit(
                    "capture",
                    captureId=capture_id,
                    source=source,
                    output=output,
                    remaining=pending.qsize(),
                )
            except Exception as exc:
                emit("error", error=f"copy capture failed: {exc}", source=source, output=output)

        time.sleep(0.01)

    if not quitting:
        emit("disconnected", error="RenderDoc target control disconnected")
finally:
    stop_input = locals().get("stop_input")
    if stop_input is not None:
        stop_input.set()
    try:
        if target is not None:
            target.Shutdown()
    except Exception:
        pass
    rd.ShutdownReplay()
