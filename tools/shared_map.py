"""Open an existing producer mapping; readers must never create a smaller one."""
import ctypes
import weakref
from ctypes import wintypes
k=ctypes.WinDLL('kernel32',use_last_error=True)
k.OpenFileMappingW.argtypes=[wintypes.DWORD,wintypes.BOOL,wintypes.LPCWSTR]
k.OpenFileMappingW.restype=wintypes.HANDLE
k.MapViewOfFile.argtypes=[wintypes.HANDLE,wintypes.DWORD,wintypes.DWORD,wintypes.DWORD,ctypes.c_size_t]
k.MapViewOfFile.restype=ctypes.c_void_p
k.UnmapViewOfFile.argtypes=[ctypes.c_void_p]
k.CloseHandle.argtypes=[wintypes.HANDLE]
def open_existing(name,size):
 handle=k.OpenFileMappingW(4,False,name)
 if not handle:raise OSError(ctypes.get_last_error(),f'Producer not ready: {name}')
 address=k.MapViewOfFile(handle,4,0,0,size)
 if not address:
  error=ctypes.get_last_error();k.CloseHandle(handle);raise OSError(error,f'Cannot map {name}')
 data=(ctypes.c_ubyte*size).from_address(address)
 def close():k.UnmapViewOfFile(address);k.CloseHandle(handle)
 weakref.finalize(data,close)
 return data
