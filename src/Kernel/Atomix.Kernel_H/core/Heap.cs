﻿/* Copyright (C) Atomix Development, Inc - All Rights Reserved
 * Unauthorized copying of this file, via any medium is strictly prohibited
 * Proprietary and confidential
 * Written by Aman Priyadarshi <aman.eureka@gmail.com>, December 2014
 * 
 * Heap.cs
 *      Heap Memory Manager
 *      
 *      History:
 *          20-12-14    File Created    Aman Priyadarshi
 *          28-05-14    Heap Manager    Aman Priyadarshi
 */

using System;
using System.Runtime.InteropServices;

using Atomix.Kernel_H.core;
using Atomix.Kernel_H.arch.x86;

using Atomix.CompilerExt;
using Atomix.CompilerExt.Attributes;

using Atomix.Assembler;
using Atomix.Assembler.x86;
using Core = Atomix.Assembler.AssemblyHelper;

namespace Atomix.Kernel_H.core
{
    public static unsafe class Heap
    {
        private static uint HeapStart = 0;
        private static uint HeapCurrent = 0;
        private static uint HeapEnd = 0;

        /// <summary>
        /// Keep track of free block address and there size (contigous) in the memory
        /// These array should not free up, and hence resides in old heap space
        /// </summary>
        private static uint[] BlockAddress;
        private static uint[] BlockSize;

#warning Heap Manager Assumption
        private const int HeapManagerSize = 1024 * 16;//~16K items, complete assumption, so should take care of this
        private static int HeapManagerPosition = 0;
        private static bool HeapManagerSetup = false;

        private static int HEAP_RESOURCE_ID;

        public static void Init(uint InitHeap)
        {
            HeapStart = InitHeap;
            HeapCurrent = InitHeap;
            HeapEnd = HeapStart + 0x100000;//Completely Assumption
            Debug.Write("Heap Initialized!!\n");
            Debug.Write("       Start Address::%d\n", InitHeap);
            Debug.Write("       End Address  ::%d\n", HeapEnd);

            //Allocate memory for future heap manager
            BlockAddress = new uint[HeapManagerSize];//64KB
            BlockSize = new uint[HeapManagerSize];//64KB

            HEAP_RESOURCE_ID = Scheduler.GetResourceID();
        }

        public static void Setup(uint Start, uint End)
        {
            HeapStart = Start;
            HeapCurrent = Start;
            HeapEnd = End;

            Debug.Write("New Heap Setup!!\n");
            Debug.Write("       Start Address::%d\n", Start);
            Debug.Write("       End Address  ::%d\n", End);

            //Assign rest of memory as free
            BlockAddress[0] = HeapStart;
            BlockSize[0] = HeapEnd - HeapStart;
            HeapManagerPosition = 1;
            HeapManagerSetup = true;
        }

        [Label("Heap")]
        public static uint kmalloc(uint len)
        {            
            if (!HeapManagerSetup)
            {
                if (HeapCurrent + len > HeapEnd)
                {
                    Debug.Write("Memory out of run before real heap :(");
                }
                uint tmp = HeapCurrent;
                HeapCurrent += len;
                Clear(tmp, len);
                return tmp;
            }
            else
            {
                return kmalloc(len, false);
            }
        }

        public static uint kmalloc(uint len, bool Aligned)
        {
            //If Heap Manager is not setup then use our old heap alogrithm -- Basically paging is calling this
            if (Aligned && !HeapManagerSetup)
            {
                if ((HeapCurrent & 0xFFFFF000) != HeapCurrent)
                {
                    HeapCurrent = (HeapCurrent & 0xFFFFF000) + 0x1000;
                }
                return kmalloc(len);
            }
            //Because access of same array from different threads can cause unexpected result -- So lock this thread
            Scheduler.SpinLock(HEAP_RESOURCE_ID);
            
            //Find a suitable hole
            int iterator;
            for (iterator = 0; iterator < HeapManagerPosition; iterator++)
            {
                if (Aligned)
                {
                    uint pos = BlockAddress[iterator];
                    uint size = BlockSize[iterator];
                    uint offset = 0;
                    if ((pos & 0xFFFFF000) != pos)
                    {
                        //Not aligned
                        offset = (pos & 0xFFFFF000) - pos + 0x1000;
                    }

                    //Check if we fit?
                    if (size >= len + offset)
                        break;//Yes :)
                }
                else if (BlockSize[iterator] >= len)
                    break;//Yes :)
            }
            
            if (iterator == HeapManagerPosition) //No block to allocate :(
            {
                Debug.Write("Memory out of run :(\n");
                while (true) ;
            }

            //So, memory need to be aligned?
            if (Aligned)
                return malloc_aligned(iterator, len);
            
            //So we have a block, right?
            {
                uint Address = BlockAddress[iterator];
                uint Size = BlockSize[iterator];
                if (Size > len)//we have to split the block
                {
                    uint Add2 = Address + len;
                    uint Size2 = Size - len;
                    for (int i = 0; i <= iterator; i++)
                    {
                        if (BlockSize[i] > Size2)
                        {
                            //here we have to put this

                            //Shift everything else
                            for (int j = iterator; j >= i + 1; j--)
                            {
                                BlockSize[j] = BlockSize[j - 1];
                                BlockAddress[j] = BlockAddress[j - 1];
                            }
                            BlockSize[i] = Size2;//Now put this element and come out
                            BlockAddress[i] = Add2;
                            break;
                        }
                    }
                }
                else//we find a perfect size
                {
                    //Remove this from free and return it
                    iterator++;
                    for ( ; iterator < HeapManagerPosition; iterator++)
                    {
                        BlockAddress[iterator - 1] = BlockAddress[iterator];
                        BlockSize[iterator - 1] = BlockSize[iterator];
                    }
                    HeapManagerPosition--;//Reduce size of array, no need to clear last empty because we never read it                    
                }
                Scheduler.SpinUnlock(HEAP_RESOURCE_ID);
                Clear(Address, len);//Clear the memory and reture
                return Address;
            }
        }

        private static uint malloc_aligned(int iterator, uint len)
        {
            uint Address = BlockAddress[iterator];
            uint Size = BlockSize[iterator];
            
            uint pos = Address;
            if ((Address & 0xFFFFF000) != Address)
            {
                pos = (Address & 0xFFFFF000) + 0x1000;//Align it first
            }

            uint NewSize = (pos - Address);
            if (NewSize != 0)//Maybe it is not aligned left, so mark left part free
            {
                for (int i = 0; i <= iterator; i++)
                {
                    if (BlockSize[i] > NewSize)
                    {
                        for (int j = iterator; j >= i + 1; j--)
                        {
                            BlockSize[j] = BlockSize[j - 1];
                            BlockAddress[j] = BlockAddress[j - 1];
                        }
                        BlockSize[i] = NewSize;
                        BlockAddress[i] = Address;
                        break;
                    }
                }
            }
            
            NewSize = Size - len - NewSize;//End block
            Address = pos + len;
            if (NewSize != 0)//Free up end part of this too
            {
                int i;
                for (i = 0; i <= iterator; i++)
                {
                    if (BlockSize[i] > NewSize)
                    {
                        for (int j = iterator; j >= i + 1; j--)
                        {
                            BlockSize[j] = BlockSize[j - 1];
                            BlockAddress[j] = BlockAddress[j - 1];
                        }
                        BlockSize[i] = NewSize;
                        BlockAddress[i] = Address;
                        break;
                    }
                }

                if (i > iterator)
                {
                    //we are at the end
                    BlockSize[HeapManagerPosition] = NewSize;
                    BlockAddress[HeapManagerPosition] = Address;
                    HeapManagerPosition++;
                }
            }
            Scheduler.SpinUnlock(HEAP_RESOURCE_ID);       
            Clear(pos, len);
            return pos;
        }

        public static void Free(object[] objs)
        {
            for (int i = 0; i < objs.Length; i++)
                Free(objs[i]);
            Free((object)objs);
        }

        /// <summary>
        /// Clear Object class and Array type objects
        /// </summary>
        /// <param name="obj"></param>
        [Assembly(0x4)]
        public static unsafe void Free(object obj)
        {
            var xEndlbl = Label.PrimaryLabel + ".End";
            var xLabel_Object = Label.PrimaryLabel + ".object";

            //EAX = object address
            Core.AssemblerCode.Add(new Mov { DestinationReg = Registers.ECX, SourceReg = Registers.EBP, SourceDisplacement = 0x8, SourceIndirect = true });
            Core.AssemblerCode.Add(new Mov { DestinationReg = Registers.EBX, SourceReg = Registers.ECX, SourceDisplacement = 0x4, SourceIndirect = true });
            Core.AssemblerCode.Add(new Cmp { DestinationReg = Registers.EBX, SourceRef = "0x1" });
            Core.AssemblerCode.Add(new Jmp { Condition = ConditionalJumpEnum.JE, DestinationRef = xLabel_Object });
            Core.AssemblerCode.Add(new Cmp { DestinationReg = Registers.EBX, SourceRef = "0x2" });
            Core.AssemblerCode.Add(new Jmp { Condition = ConditionalJumpEnum.JNE, DestinationRef = xEndlbl });
            /* Array :-
             * According to compiler layout is:
             * 1) Type Signature
             * 2) Magic 0x2 -- 0x4
             * 3) Number of elements -- 0x8
             * 4) Size of each element -- 0xC
             */
            Core.AssemblerCode.Add(new Mov { DestinationReg = Registers.EAX, SourceReg = Registers.ECX, SourceDisplacement = 0x8, SourceIndirect = true });
            Core.AssemblerCode.Add(new Multiply { DestinationReg = Registers.ECX, DestinationDisplacement = 0xC, DestinationIndirect = true });
            Core.AssemblerCode.Add(new Add { DestinationReg = Registers.EAX, SourceRef = "0x10" });//Header            
            Core.AssemblerCode.Add(new Jmp { DestinationRef = xEndlbl });

            Core.AssemblerCode.Add(new Label(xLabel_Object));
            /* Object :-
             * According to compiler layout is:
             * 1) Type Signature
             * 2) Magic 0x1 -- 0x4
             * 3) Total Size -- 0x8
             */
            Core.AssemblerCode.Add(new Mov { DestinationReg = Registers.EAX, SourceReg = Registers.ECX, SourceDisplacement = 0x8, SourceIndirect = true });

            Core.AssemblerCode.Add(new Label(xEndlbl));
            Core.AssemblerCode.Add(new Push { DestinationReg = Registers.ECX });//Address
            Core.AssemblerCode.Add(new Push { DestinationReg = Registers.EAX });//Length
            Core.AssemblerCode.Add(new Call("System_Void_Atomix_Kernel_H_core_Heap_Free_System_UInt32__System_UInt32_"));
        }

        [Plug("System_Void_Atomix_Kernel_H_core_Heap_Free_System_UInt32__System_UInt32_")]
        public static void Free(uint Address, uint len)
        {
            //Because access of same array from different threads can cause unexpected result -- So spin lock this thread
            Scheduler.SpinLock(HEAP_RESOURCE_ID);

            //Check if any block can fit to left/Right of this
            int iterator, left = -1, right = -1;
            for (iterator = 0; iterator < HeapManagerPosition; iterator++)
            {
                uint Add = BlockAddress[iterator];
                uint Size = BlockSize[iterator];                
                if (Add + Size == Address)
                {
                    left = iterator;
                }
                else if (Add == Address + len)
                {
                    right = iterator;
                }
            }

            //Compute new address and new size of block
            uint NewAddress = Address;
            uint NewSize = len;
            if (left != -1)
            {
                NewAddress = BlockAddress[left];
                NewSize += BlockSize[left];
            }

            if (right != -1)
            {
                NewSize += BlockSize[right];
            }
                        
            //Remove left and right blocks
            int lastempty = 0;
            for (iterator = 0; iterator < HeapManagerPosition; iterator++)
            {
                if (iterator == left ||
                    iterator == right)
                    continue;
                BlockAddress[lastempty] = BlockAddress[iterator];
                BlockSize[lastempty] = BlockSize[iterator];
                lastempty++;
            }
            HeapManagerPosition = lastempty;

            //Add our new block to memory
            for (iterator = 0; iterator < HeapManagerPosition; iterator++)
            {
                if (BlockSize[iterator] > NewSize)
                {
                    //here we have to put this

                    //Shift everything else
                    for (int j = HeapManagerPosition; j >= iterator + 1; j--)
                    {
                        BlockSize[j] = BlockSize[j - 1];
                        BlockAddress[j] = BlockAddress[j - 1];
                    }
                    BlockSize[iterator] = NewSize;//Now put this element and come out
                    BlockAddress[iterator] = NewAddress;
                    HeapManagerPosition++;
                    iterator = -1;//Flag
                    break;
                }
            }
            if (iterator != -1)//End of loop
            {
                BlockSize[HeapManagerPosition] = NewSize;
                BlockAddress[HeapManagerPosition] = NewAddress;
                HeapManagerPosition++;
            }
            Scheduler.SpinUnlock(HEAP_RESOURCE_ID);
        }
        
        private static unsafe void Clear(uint Address, uint ByteCount)
        {
#warning add a fast method to clear up memory by using literal assembly codes :P
            var xAddress = (uint*)Address;
            for (int i = 0; i < ByteCount/4; i++)
                xAddress[i] = 0x0;
            var xadd2 = (byte*)(Address + (ByteCount/4));
            for (int i = 0; i < ByteCount % 4; i++)
                xadd2[i] = 0x0;
        }
    }
}