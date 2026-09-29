"""Owned, animated X11 window for actual window-capture integration (not xemu)."""
import asyncio
import ctypes
import ctypes.util
import os
import tkinter
import time
from collections import OrderedDict

class WindowFixture:
    def __init__(self):
        self.root=tkinter.Tk()
        self.root.title('Xemu runner media capture fixture')
        self.root.geometry('640x480+40+30')
        self.canvas=tkinter.Canvas(self.root,bg='#205080',highlightthickness=0)
        self.canvas.pack(fill='both',expand=True)
        self.box=self.canvas.create_rectangle(20,350,80,410,fill='#f08020',outline='')
        self.barcode=[self.canvas.create_rectangle(20+i*10,20,30+i*10,40,fill='black',outline='') for i in range(32)]
        self.paint_times=OrderedDict()
        self.root.update()
        self.window_id=self.root.winfo_id()
        lib=ctypes.CDLL(ctypes.util.find_library('X11'))
        lib.XOpenDisplay.argtypes=[ctypes.c_char_p];lib.XOpenDisplay.restype=ctypes.c_void_p
        lib.XInternAtom.argtypes=[ctypes.c_void_p,ctypes.c_char_p,ctypes.c_int];lib.XInternAtom.restype=ctypes.c_ulong
        lib.XChangeProperty.argtypes=[ctypes.c_void_p,ctypes.c_ulong,ctypes.c_ulong,ctypes.c_ulong,ctypes.c_int,ctypes.c_int,ctypes.c_void_p,ctypes.c_int]
        lib.XCloseDisplay.argtypes=[ctypes.c_void_p]
        display=lib.XOpenDisplay(None)
        if not display:raise RuntimeError('X11 fixture requires DISPLAY, e.g. xvfb-run -a')
        atom=lib.XInternAtom(display,b'_NET_WM_PID',0)
        pid=ctypes.c_ulong(os.getpid())
        lib.XChangeProperty(display,self.window_id,atom,6,32,0,ctypes.byref(pid),1)
        lib.XCloseDisplay(display)
        self.task=asyncio.create_task(self.animate())

    async def animate(self):
        frame=0
        while True:
            submitted=time.monotonic()
            code=frame & 65535
            for i,rectangle in enumerate(self.barcode):
                bit=((code if i<16 else code^65535)>>(i%16))&1
                self.canvas.itemconfigure(rectangle,fill='white' if bit else 'black')
            self.paint_times[code]=submitted
            if len(self.paint_times)>512:self.paint_times.popitem(last=False)
            x=20+(frame*4)%480
            self.canvas.coords(self.box,x,350,x+60,410)
            self.root.update()
            frame+=1
            await asyncio.sleep(1/60)

    def resize(self):
        self.root.geometry('800x450+40+30')
        self.root.update()

    async def close(self):
        self.task.cancel()
        await asyncio.gather(self.task,return_exceptions=True)
        self.root.destroy()
