// Copyright (c) 2014 AlphaSierraPapa for the SharpDevelop Team
// 
// Permission is hereby granted, free of charge, to any person obtaining a copy of this
// software and associated documentation files (the "Software"), to deal in the Software
// without restriction, including without limitation the rights to use, copy, modify, merge,
// publish, distribute, sublicense, and/or sell copies of the Software, and to permit persons
// to whom the Software is furnished to do so, subject to the following conditions:
// 
// The above copyright notice and this permission notice shall be included in all copies or
// substantial portions of the Software.
// 
// THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR IMPLIED,
// INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY, FITNESS FOR A PARTICULAR
// PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE AUTHORS OR COPYRIGHT HOLDERS BE LIABLE
// FOR ANY CLAIM, DAMAGES OR OTHER LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR
// OTHERWISE, ARISING FROM, OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER
// DEALINGS IN THE SOFTWARE.
using System;
using System.Globalization;
using System.Runtime.InteropServices;

namespace ICSharpCode.Profiler.Controller
{
	/// <summary>
	/// Contains general fields for verification, synchronisation and important addresses and offsets.
	/// </summary>
	[StructLayout(LayoutKind.Sequential)]
	struct SharedMemoryHeader64
	{
		public int Magic;

		// Verfication value, always '~SM1'
		public volatile int ExclusiveAccess;

		public int TotalLength;

		public int NativeToManagedBufferOffset;

		public int ThreadDataOffset;

		public int ThreadDataLength;

		public int HeapOffset;

		public int HeapLength;

		public TargetProcessPointer64 NativeAddress;

		public TargetProcessPointer64 RootFuncInfoAddress;

		public TargetProcessPointer64 LastThreadListItem;

		public int ProcessorFrequency;

		public bool DoNotProfileDotnetInternals;

		public bool CombineRecursiveFunction;

		public bool TrackEvents;

		public Allocator64 Allocator;
	}
	/// <summary>
	/// The representation of the unmanaged allocator used by the Hook.
	/// </summary>
	[StructLayout(LayoutKind.Sequential)]
	unsafe struct Allocator64
	{
		public TargetProcessPointer64 startPos;

		public TargetProcessPointer64 pos;

		public TargetProcessPointer64 endPos;

		public fixed UInt64 freeList[32];

		// Need UInt32 instead of TargetProcessPointer32
		// because of fixed
		public static void ClearFreeList(Allocator64* a)
		{
			for (int i = 0; i < 32; i++) {
				a->freeList[i] = 0;
			}
		}
	}
	/// <summary>
	/// 32bit pointer used when a 32bit executable is profiled.
	/// </summary>
	[StructLayout(LayoutKind.Sequential)]
	struct TargetProcessPointer64
	{
		public UInt64 Pointer;

		public override string ToString()
		{
			return "0x" + Pointer.ToString("x", CultureInfo.InvariantCulture);
		}

		public static implicit operator TargetProcessPointer(TargetProcessPointer64 p) {
			return new TargetProcessPointer(p);
		}

		public static TargetProcessPointer64 operator +(TargetProcessPointer64 p, Int64 offset) {
			unchecked {
				p.Pointer += (UInt64)offset;
			}
			return p;
		}

		public static TargetProcessPointer64 operator -(TargetProcessPointer64 p, Int64 offset) {
			unchecked {
				p.Pointer -= (UInt64)offset;
			}
			return p;
		}

		public static Int64 operator -(TargetProcessPointer64 ptr1, TargetProcessPointer64 ptr2) {
			unchecked {
				return (Int64)(ptr1.Pointer - ptr2.Pointer);
			}
		}
	}
	/// <summary>
	/// The managed version of the FunctionInfo
	/// </summary>
	unsafe partial struct FunctionInfo
	{
		[System.Diagnostics.CodeAnalysis.SuppressMessage("Microsoft.Usage", "CA2201:DoNotRaiseReservedExceptionTypes")]
		public static TargetProcessPointer64* GetChildren64(FunctionInfo* f)
		{
			if (f == null)
				throw new NullReferenceException();
			return (TargetProcessPointer64*)(f + 1);
		}

		public static void AddOrUpdateChild64(FunctionInfo* parent, FunctionInfo* child, Profiler profiler)
		{
			int slot = child->Id;
			while (true) {
				slot &= parent->LastChildIndex;
				FunctionInfo* slotContent = (FunctionInfo*)profiler.TranslatePointer(GetChildren64(parent)[slot]);
				if (slotContent == null || slotContent->Id == child->Id) {
					GetChildren64(parent)[slot] = profiler.TranslatePointerBack64(child);
					break;
				}
				slot++;
			}
		}
	}
	/// <summary>
	/// The managed version of the ThreadLocalData
	/// </summary>
	[StructLayout(LayoutKind.Sequential)]
	unsafe struct ThreadLocalData64
	{
		public int ThreadID;

		public volatile int InLock;

		public volatile bool Active;

		public TargetProcessPointer64 Predecessor;

		public TargetProcessPointer64 Follower;

		public LightweightStack64 Stack;
	}
	[StructLayout(LayoutKind.Sequential)]
	unsafe struct LightweightStack64
	{
		public TargetProcessPointer64 Array;

		public TargetProcessPointer64 TopPointer;

		public TargetProcessPointer64 ArrayEnd;
	}
	[StructLayout(LayoutKind.Sequential)]
	unsafe struct StackEntry64
	{
		public TargetProcessPointer64 Function;

		public ulong StartTime;

		public int FrameCount;
	}
}
