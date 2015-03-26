﻿using System;
using System.Collections.Generic;

using Atomix.Kernel_H.core;
using Atomix.Kernel_H.devices;

using Atomix.CompilerExt;
using Atomix.CompilerExt.Attributes;

using Atomix.Assembler;
using Atomix.Assembler.x86;
using Core = Atomix.Assembler.AssemblyHelper;

namespace Atomix.Kernel_H.core
{
    public static class Task
    {
        [Plug("__Switch_Task__")]
        public static uint SwitchTask(uint oldStack)
        {
            //Increment System Timer
            Timer.Tick();
            
            return Scheduler.SwitchTask(oldStack);
        }

        [Assembly, Plug("__ISR_Handler_20")]
        private static void SetupIRQ0()
        {
            //Clear Interrupts
            Core.AssemblerCode.Add(new Cli());

            //Push all the Registers
            Core.AssemblerCode.Add(new Pushad());
            
            //Push ESP
            Core.AssemblerCode.Add(new Push { DestinationReg = Registers.ESP });
            Core.AssemblerCode.Add(new Call("__Switch_Task__"));

            //Get New task ESP
            Core.AssemblerCode.Add(new Pop { DestinationReg = Registers.ESP });

            //Tell CPU that we have recieved IRQ
            Core.AssemblerCode.Add(new Mov { DestinationReg = Registers.AL, SourceRef = "0x20", Size = 8 });
            Core.AssemblerCode.Add(new Out { DestinationRef = "0x20", SourceReg = Registers.AL });
            
            //Load Registers
            Core.AssemblerCode.Add(new Popad());

            //Enable Interrupts
            Core.AssemblerCode.Add(new Sti());

            Core.AssemblerCode.Add(new Iret());
        }
    }
}
