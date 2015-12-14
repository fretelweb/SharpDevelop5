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
using System.Linq;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using ICSharpCode.Profiler.Controller.Data;
using ICSharpCode.Profiler.Interprocess;
using Microsoft.Win32;

namespace ICSharpCode.Profiler.Controller
{
	/// <summary>
	/// The core class of the profiler.
	/// </summary>
	public unsafe sealed partial class Profiler : IDisposable
	{
		[DllImport("Hook64.dll", EntryPoint = "rdtsc"), System.Security.SuppressUnmanagedCodeSecurityAttribute]
		[System.Diagnostics.CodeAnalysis.SuppressMessage("Microsoft.Design", "CA1060:MovePInvokesToNativeMethodsClass")]
		static extern void Rdtsc64(out ulong value);

		// 4 kb
		// 16 mb
		// needs to be stored in member variable to prevent GC from cleaning up the memory mapped file
		// before the profilee can open it.
		void InitializeHeader64()
		{
			memHeader64 = (SharedMemoryHeader64*)fullView.Pointer;
			#if DEBUG
						// '~DBG'
			memHeader32->Magic = 0x7e444247;
			#else
			// '~SM1'
			memHeader64->Magic = 0x7e534d31;
			#endif
			memHeader64->TotalLength = profilerOptions.SharedMemorySize;
			memHeader64->NativeToManagedBufferOffset = Align(sizeof(SharedMemoryHeader64));
			memHeader64->ThreadDataOffset = Align(memHeader64->NativeToManagedBufferOffset + bufferSize);
			memHeader64->ThreadDataLength = threadDataSize;
			memHeader64->HeapOffset = Align(memHeader64->ThreadDataOffset + threadDataSize);
			memHeader64->HeapLength = profilerOptions.SharedMemorySize - memHeader64->HeapOffset;
			memHeader64->ProcessorFrequency = GetProcessorFrequency();
			memHeader64->DoNotProfileDotnetInternals = profilerOptions.DoNotProfileDotNetInternals;
			memHeader64->CombineRecursiveFunction = profilerOptions.CombineRecursiveFunction;
			memHeader64->TrackEvents = profilerOptions.TrackEvents;
			if ((Int64)(fullView.Pointer + memHeader64->HeapOffset) % 8 != 0) {
				throw new DataMisalignedException("Heap is not aligned properly: " + ((Int64)(fullView.Pointer + memHeader64->HeapOffset)).ToString(CultureInfo.InvariantCulture) + "!");
			}
		}

		void CollectData64()
		{
			if (TranslatePointer(memHeader64->RootFuncInfoAddress) == null)
				return;
			ulong now = GetRdtsc();
			ThreadLocalData64* item = (ThreadLocalData64*)TranslatePointer(memHeader64->LastThreadListItem);
			List<Stack<int>> stackList = new List<Stack<int>>();
			while (item != null) {
				StackEntry64* entry = (StackEntry64*)TranslatePointer(item->Stack.Array);
				Stack<int> itemIDs = new Stack<int>();
				while (entry != null && entry <= (StackEntry64*)TranslatePointer(item->Stack.TopPointer)) {
					FunctionInfo* function = (FunctionInfo*)TranslatePointer(entry->Function);
					itemIDs.Push(function->Id);
					function->TimeSpent += now - entry->StartTime;
					entry++;
				}
				stackList.Add(itemIDs);
				item = (ThreadLocalData64*)TranslatePointer(item->Predecessor);
			}
			if (enableDC) {
				AddDataset(fullView.Pointer, memHeader64->NativeAddress + memHeader64->HeapOffset, memHeader64->Allocator.startPos - memHeader64->NativeAddress, memHeader64->Allocator.pos - memHeader64->Allocator.startPos, isFirstDC, memHeader64->RootFuncInfoAddress);
				isFirstDC = false;
			}
			ZeroMemory(new IntPtr(TranslatePointer(memHeader64->Allocator.startPos)), new IntPtr(memHeader64->Allocator.pos - memHeader64->Allocator.startPos));
			memHeader64->Allocator.pos = memHeader64->Allocator.startPos;
			Allocator64.ClearFreeList(&memHeader64->Allocator);
			FunctionInfo* root = CreateFunctionInfo(0, 0, stackList.Count);
			memHeader64->RootFuncInfoAddress = TranslatePointerBack64(root);
			item = (ThreadLocalData64*)TranslatePointer(memHeader64->LastThreadListItem);
			now = GetRdtsc();
			foreach (Stack<int> thread in stackList) {
				FunctionInfo* child = null;
				StackEntry64* entry = (StackEntry64*)TranslatePointer(item->Stack.TopPointer);
				while (thread.Count > 0) {
					FunctionInfo* stackItem = CreateFunctionInfo(thread.Pop(), 0, child != null ? 1 : 0);
					if (child != null)
						FunctionInfo.AddOrUpdateChild64(stackItem, child, this);
					entry->Function = TranslatePointerBack64(stackItem);
					entry->StartTime = now;
					entry--;
					child = stackItem;
				}
				if (child != null)
					FunctionInfo.AddOrUpdateChild64(root, child, this);
				item = (ThreadLocalData64*)TranslatePointer(item->Predecessor);
			}
		}

		unsafe void* Malloc64(int bytes)
		{
			#if DEBUG
						const int debuggingInfoSize = 8;
			bytes += debuggingInfoSize;
			#endif
			void* t = TranslatePointer(memHeader64->Allocator.pos);
			memHeader64->Allocator.pos += bytes;
			#if DEBUG
						t = (byte*)t + debuggingInfoSize;
			((Int32*)t)[-1] = bytes - debuggingInfoSize;
			#endif
			return t;
		}

		bool AllThreadsWait64()
		{
			try {
				threadListMutex.WaitOne();
			}
			catch (AbandonedMutexException) {
				// profilee crashed while holding the thread list mutex
				return true;
			}
			bool isWaiting = true;
			ThreadLocalData64* item = (ThreadLocalData64*)TranslatePointer(memHeader64->LastThreadListItem);
			while (item != null) {
				if (item->InLock == 1)
					isWaiting = false;
				item = (ThreadLocalData64*)TranslatePointer(item->Predecessor);
			}
			threadListMutex.ReleaseMutex();
			return isWaiting;
		}

		internal unsafe void* TranslatePointer64(TargetProcessPointer64 ptr)
		{
			if (ptr.Pointer == 0)
				return null;
			// Use Int32 instead of int because of preprocessor!
			unchecked {
				Int64 spaceDiff = (Int64)(new IntPtr(fullView.Pointer)) - (Int64)memHeader64->NativeAddress.Pointer;
				return new IntPtr((Int64)ptr.Pointer + spaceDiff).ToPointer();
			}
		}

		internal unsafe TargetProcessPointer64 TranslatePointerBack64(void* ptr)
		{
			// Use Int32 instead of int because of preprocessor!
			if (ptr == null)
				return new TargetProcessPointer64();
			unchecked {
				Int64 spaceDiff = (Int64)(new IntPtr(fullView.Pointer)) - (Int64)memHeader64->NativeAddress.Pointer;
				TargetProcessPointer64 pointer = new TargetProcessPointer64();
				pointer.Pointer = (UInt64)((Int64)ptr - spaceDiff);
				return pointer;
			}
		}
	#region IDisposable Member
	#endregion
	#region UnmanagedProfilingDataSet implementation
	#endregion
	}
}
