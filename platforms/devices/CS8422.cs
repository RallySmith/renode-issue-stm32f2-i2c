// Simple model to provide Cirrus Logic CS8422 basic I2C support
//
// Copyright (c) 2023-2024 eCosCentric Limited
//
// uncomment the following line to get warnings about
// accessing unhandled registers; it's disabled by default
// as it generates a lot of noise in the log and might
// hide some important messages
// 
#define WARN_UNHANDLED_REGISTERS

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Antmicro.Renode.Logging;
using Antmicro.Renode.Peripherals.I2C;
//using Antmicro.Renode.Peripherals.Bus;
using Antmicro.Renode.Utilities;
using Antmicro.Renode.Exceptions;
using Antmicro.Renode.Core;
using Antmicro.Renode.Core.Structure.Registers;

// Model based on datasheet DS692F3
// We currently ignore the SPI connectivity option.
//
// I2C mode
//
// Write        {START} [CHIPADDR:0] {ACK} [MAP] {ACK} [DATA0] {ACK} ... [DATAn] {ACK} {STOP}
//
// Read (setup) {START} [CHIPADDR:0] {ACK} [MAP] {ACK} {STOP}
// Read (data)  {START} [CHIPADDR:1] {ACK} [DATA0] {ACK} ... [DATAn] {NAK} {STOP}

namespace Antmicro.Renode.Peripherals.I2C
{
    public class CS8422 : II2CPeripheral, IGPIOReceiver
    {
        public CS8422(string revision = "B1")
        {
            if(("B1" != revision) && ("A" != revision))
            {
                throw new ConstructionException("invalid revision");
            }
            this.Revision = revision;
            // NOTE: GPO[0..3] could be provided as GPIO signals.
            //       e.g. GPO0 = new GPIO();
            //       However, functionality not needed for current
            //       board hardware being simulated.

            registers = CreateRegisters();
            Reset();
        }

        public void Reset()
        {
            autoIncrement = false;
            lastRegister = 0;

            // reg   rev A      rev B1 (on EB00371)
            // ----  -----      -------------------
            // 0x01  0x10       0x12                    **
            // 0x02  0x80       0x80
            // 0x03  0x08       0x10                    **
            // 0x04  0x04       0x04
            // 0x05  0x00       0x00
            // 0x06  0x00       0x00
            // 0x07  0x40       0x40
            // 0x08  0x60       0x40                    **
            // 0x09  0x08       0x08
            // 0x0A  0x10       0x10
            // 0x0B  0x00       0x00
            // 0x0C  0x00       0x00
            // 0x0D  0x00       0x00
            // 0x0E  0x00       0x00
            // 0x0F  0x00       0x00
            // 0x10  0x00       0x00
            // 0x16  0b0xxxxxxx 0b0xxxxxxx

            if ("A" == Revision)
            {
                DevID = 0x10;
            }
            if ("B1" == Revision)
            {
                DevID = 0x12;
            }
            registers.Reset();
        }

        public void Write(byte[] data)
        {
            if(data.Length == 0)
            {
                this.Log(LogLevel.Warning, "Unexpected write with no data");
                return;
            }

            this.Log(LogLevel.Noisy, "Write {0}", data.Select (x=>"0x"+x.ToString("X2")).Aggregate((x,y)=>x+" "+y));

            autoIncrement = false;
            if(0 != (data[0] & (1 << 7)))
            {
                autoIncrement = true;
            }
            lastRegister = (Registers)(data[0] & 0x7F);
            this.Log(LogLevel.Noisy, "Setting register to 0x{0:X} autoIncrement {1}", lastRegister, autoIncrement);

            if(0 != (data.Length - 1))
            {
                for (var i = 1; (i < data.Length); i++)
                {
                    this.Log(LogLevel.Debug, "Write register 0x{0:X} value 0x{1:X}", lastRegister, data[i]);
                    registers.Write((long)lastRegister, data[i]);
                    if (autoIncrement)
                    {
                        AdvanceRegister();
                    }
                }
            }
        }

        public byte[] Read(int count)
        {
            this.Log(LogLevel.Noisy, "Reading {0} bytes from register 0x{1:X} autoIncrement {2}", count, lastRegister, autoIncrement);

            var result = new List<byte>();
            for (var i = 0; (i < count); i++)
            {
                result.Add(ReadCurrentRegister());
                if(autoIncrement)
                {
                    AdvanceRegister();
                }
            }

            this.Log(LogLevel.Noisy, "Read result: {0}", Misc.PrettyPrintCollectionHex(result));
            return result.ToArray();
        }

        private void AdvanceRegister()
        {
            lastRegister = (Registers)((int)lastRegister + 1);
            // From H/W checks against a CS8422 Rev2 device the MAP
            // wraps at 0x7F and reads from undefined registers return
            // 0x00.
            if(0x7F < (int)lastRegister)
            {
                lastRegister = 0;
            }
            this.Log(LogLevel.Noisy, "Auto-incrementing to the next register 0x{0:X}", lastRegister);
        }

        private byte ReadCurrentRegister()
        {
            byte rval = registers.Read((long)lastRegister);
            this.Log(LogLevel.Debug, "ReadCurrentRegister: lastRegister {0} rval 0x{1:X}", lastRegister, rval);
            return rval;
        }

        public void FinishTransmission()
        {
        }

        public void OnGPIO(int number, bool value)
        {
            if (0 != number)
            {
                this.Log(LogLevel.Error, "Unexpected nRST connection {0}", number);
                return;
            }
            this.Log(LogLevel.Debug, "OnGPIO: number {0} value {1}", number, value);
            // 0==number: active-LOW nRST signal

            // When LOW the CS8422 enters a low-power state and all
            // internal states are reset. This method will only be
            // called on a HIGH->LOW transition:
            if (false == value)
            {
                this.Log(LogLevel.Info, "Resetting");
                Reset();
            }
        }

        public string Revision { get; }

        private ByteRegisterCollection CreateRegisters()
        {
            var registersMap = new Dictionary<long, ByteRegister>
            {
                { (long)Registers.ChipIdVersion, new ByteRegister(this, 0x12)
                  .WithValueField(0, 3, valueProviderCallback: _ => ((ulong)DevID & 0x7), name: "REV")
                  .WithValueField(3, 5, valueProviderCallback: _ => 0x02, name: "ID")
                },
                { (long)Registers.ClockControl, new ByteRegister(this, 0x80)
                  .WithReservedBits(0, 1)
                  .WithValueField(1, 2, name: "INT")
                  .WithValueField(3, 2, name: "RMCK_CTL")
                  .WithFlag(5, name: "SWCLK")
                  .WithFlag(6, name: "FSWCLK")
                  .WithFlag(7, name: "PDN")
                },
                { (long)Registers.ReceiverInputControl, new ByteRegister(this, 0x10) // TODO: rev "A1" default 0x08
                  .WithReservedBits(0, 2)
                  .WithFlag(2, out var input_type, name: "INPUT_TYPE")
                  .WithValueField(3, 2, name: "TXSEL")
                  .WithValueField(5, 2, name: "RXSEL")
                  .WithFlag(7, out var rx_mode, name: "RX_MODE")
                },
                { (long)Registers.ReceiverDataControl, new ByteRegister(this, 0x04)
                  .WithValueField(0, 3, name: "EMPH_CNTL")
                  .WithFlag(3, name: "DETCI")
                  .WithFlag(4, name: "CHS")
                  .WithValueField(5, 2, name: "HOLD")
                  .WithFlag(7, name: "TRUNC")
                },
                { (long)Registers.GPOControl1, new ByteRegister(this, 0x00)
                  .WithValueField(0, 4, name: "GPO1SEL")
                  .WithValueField(4, 4, name: "GPO0SEL")
                },
                { (long)Registers.GPOControl2, new ByteRegister(this, 0x00)
                  .WithValueField(0, 4, name: "GPO3SEL")
                  .WithValueField(4, 4, name: "GPO2SEL")
                },
                { (long)Registers.SAIClockControl, new ByteRegister(this, 0x40)
                  .WithReservedBits(0, 3)
                  .WithFlag(3, name: "SAI_MCLK")
                  .WithValueField(4, 4, name: "SAI_CLK")
                },
                { (long)Registers.SRCSAOClockControl, new ByteRegister(this, 0x40) // TODO: rev "A1" default 0x60
                  .WithFlag(0, name: "SRC_DIV")
                  .WithValueField(1, 2, name: "SRC_MCLK")
                  .WithFlag(3, name: "SAO_MCLK")
                  .WithValueField(4, 4, name: "SAO_CLK")
                },
                { (long)Registers.RMCKCtrlMisc, new ByteRegister(this, 0x08)
                  .WithReservedBits(0, 3)
                  .WithFlag(3, name: "SRC_MUTE")
                  .WithValueField(4, 4, name: "RMCK")
                },
                { (long)Registers.DataRoutingControl, new ByteRegister(this, 0x10)
                  .WithReservedBits(0, 1)
                  .WithFlag(1, name: "SRCD")
                  .WithFlag(2, name: "MUTESAO2")
                  .WithFlag(3, name: "MUTESAO1")
                  .WithValueField(4, 4, name: "SDOUT")
                },
                { (long)Registers.SAIDataFormat, new ByteRegister(this, 0x00)
                  .WithReservedBits(0, 3)
                  .WithValueField(3, 3, name: "SIFSEL")
                  .WithFlag(6, name: "SISF")
                  .WithFlag(7, name: "SIMS")
                },
                { (long)Registers.SAO1DataFormatTDM, new ByteRegister(this, 0x00)
                  .WithValueField(0, 2, name: "TDM")
                  .WithValueField(2, 2, name: "SOFSEL1")
                  .WithValueField(4, 2, name: "SORES1")
                  .WithFlag(6, name: "SOSF1")
                  .WithFlag(7, name: "SOMS1")
                },
                { (long)Registers.SAO2DataFormat, new ByteRegister(this, 0x00)
                  .WithReservedBits(0, 2)
                  .WithValueField(2, 2, name: "SOFSEL2")
                  .WithValueField(4, 2, name: "SORES2")
                  .WithFlag(6, name: "SOSF2")
                  .WithFlag(7, name: "SOMS2")
                },
                { (long)Registers.RERRUnmasking, new ByteRegister(this, 0x00)
                  .WithFlag(0, name: "PARM")
                  .WithFlag(1, name: "BIPM")
                  .WithFlag(2, name: "CONFM")
                  .WithFlag(3, name: "VM")
                  .WithFlag(4, name: "UNLOCKM")
                  .WithFlag(5, name: "CCRCM")
                  .WithFlag(6, name: "QCRCM")
                  .WithReservedBits(7, 1)
                },
                { (long)Registers.InterruptUnmasking, new ByteRegister(this, 0x00)
                  .WithFlag(0, name: "SRC_UNLOCKM")
                  .WithFlag(1, name: "FCHM")
                  .WithFlag(2, name: "QCHM")
                  .WithFlag(3, name: "RERRM")
                  .WithFlag(4, name: "CCHM")
                  .WithFlag(5, name: "DETCM")
                  .WithFlag(6, name: "QSLIPM")
                  .WithFlag(7, name: "PCCHM")
                },
                { (long)Registers.InterruptMode, new ByteRegister(this, 0x00)
                  .WithValueField(0, 2, name: "SRC_UNLOCK")
                  .WithValueField(2, 2, name: "RERR")
                  .WithReservedBits(4, 4)
                },
                { (long)Registers.ReceiverChannelStatus, new ByteRegister(this, 0x00)
                  .WithFlag(0, name: "EMPH")
                  .WithFlag(1, name: "ORIG")
                  .WithFlag(2, name: "COPY")
                  .WithFlag(3, name: "PRO")
                  .WithValueField(4, 4, name: "AUX")
                },
                { (long)Registers.FormatDetectStatus, new ByteRegister(this, 0x00)
                  .WithReservedBits(0, 2)
                  .WithFlag(2, name: "DGTL_SIL")
                  .WithFlag(3, name: "HD_CD")
                  .WithFlag(4, name: "DTS_CD")
                  .WithFlag(5, name: "DTS_LD")
                  .WithFlag(6, name: "IEC61937")
                  .WithFlag(7, name: "PCM")
                },
                { (long)Registers.ReceiverError, new ByteRegister(this, 0x00)
                  .WithFlag(0, name: "PAR")
                  .WithFlag(1, name: "BIP")
                  .WithFlag(2, name: "CONF")
                  .WithFlag(3, name: "V")
                  .WithFlag(4, name: "UNLOCK")
                  .WithFlag(5, name: "CCRC")
                  .WithFlag(6, name: "QCRC")
                  .WithReservedBits(7, 1)
                },
                { (long)Registers.InterruptStatus, new ByteRegister(this, 0x00)
                  .WithFlag(0, name: "SRC_UNLOCK")
                  .WithFlag(1, name: "FCH")
                  .WithFlag(2, name: "QCH")
                  .WithFlag(3, name: "RERR")
                  .WithFlag(4, name: "CCH")
                  .WithFlag(5, name: "DETC")
                  .WithFlag(6, name: "OSLIP")
                  .WithFlag(7, name: "PCCH")
                },
                { (long)Registers.PLLStatus, new ByteRegister(this, 0x00)
                  .WithReservedBits(0, 3)
                  .WithFlag(3, name: "192KHZ")
                  .WithFlag(4, name: "96KHZ")
                  .WithFlag(5, name: "PLL_LOCK")
                  .WithFlag(6, name: "ISCLK_ACTIVE")
                  .WithFlag(7, name: "RX_ACTIVE")
                },
                { (long)Registers.ReceiverStatus, new ByteRegister(this, 0x00)
                  .WithFlag(0, name: "BLK_PERR")
                  .WithFlag(1, name: "BLK_BERR")
                  .WithFlag(2, name: "BLK_CERR")
                  .WithFlag(3, name: "BLK_VERR")
                  .WithFlag(4, name: "RX_LOCK")
                  .WithValueField(5, 2, name: "RCVR_RATE")
                  .WithFlag(7, name: "CS_UPDATE")
                },
                { (long)Registers.FsXTIRatio0, new ByteRegister(this, 0x00)
                  .WithValueField(0, 8, name: "FS_XT_HIGH")
                },
                { (long)Registers.FsXTIRatio1, new ByteRegister(this, 0x00)
                  .WithValueField(0, 8, name: "FS_XT_LOW")
                },
                { (long)Registers.QSubcode1, new ByteRegister(this, 0x00)
                  .WithValueField(0, 4, name: "ADDRESS")
                  .WithValueField(4, 4, name: "CONTROL")
                },
                { (long)Registers.QSubcode2, new ByteRegister(this, 0x00)
                  .WithValueField(0, 8, name: "TRACK")
                },
                { (long)Registers.QSubcode3, new ByteRegister(this, 0x00)
                  .WithValueField(0, 8, name: "INDEX")
                },
                { (long)Registers.QSubcode4, new ByteRegister(this, 0x00)
                  .WithValueField(0, 8, name: "MINUTE")
                },
                { (long)Registers.QSubcode5, new ByteRegister(this, 0x00)
                  .WithValueField(0, 8, name: "SECOND")
                },
                { (long)Registers.QSubcode6, new ByteRegister(this, 0x00)
                  .WithValueField(0, 8, name: "FRAME")
                },
                { (long)Registers.QSubcode7, new ByteRegister(this, 0x00)
                  .WithValueField(0, 8, name: "ZERO")
                },
                { (long)Registers.QSubcode8, new ByteRegister(this, 0x00)
                  .WithValueField(0, 8, name: "ABS_MINUTE")
                },
                { (long)Registers.QSubcode9, new ByteRegister(this, 0x00)
                  .WithValueField(0, 8, name: "ABS_SECOND")
                },
                { (long)Registers.QSubcode10, new ByteRegister(this, 0x00)
                  .WithValueField(0, 8, name: "ABS_FRAME")
                },
                { (long)Registers.ChannelAStatus0, new ByteRegister(this, 0x00)
                  .WithValueField(0, 8, name: "AC0")
                },
                { (long)Registers.ChannelAStatus1, new ByteRegister(this, 0x00)
                  .WithValueField(0, 8, name: "AC1")
                },
                { (long)Registers.ChannelAStatus2, new ByteRegister(this, 0x00)
                  .WithValueField(0, 8, name: "AC2")
                },
                { (long)Registers.ChannelAStatus3, new ByteRegister(this, 0x00)
                  .WithValueField(0, 8, name: "AC3")
                },
                { (long)Registers.ChannelAStatus4, new ByteRegister(this, 0x00)
                  .WithValueField(0, 8, name: "AC4")
                },
                { (long)Registers.ChannelBStatus0, new ByteRegister(this, 0x00)
                  .WithValueField(0, 8, name: "BC0")
                },
                { (long)Registers.ChannelBStatus1, new ByteRegister(this, 0x00)
                  .WithValueField(0, 8, name: "BC1")
                },
                { (long)Registers.ChannelBStatus2, new ByteRegister(this, 0x00)
                  .WithValueField(0, 8, name: "BC2")
                },
                { (long)Registers.ChannelBStatus3, new ByteRegister(this, 0x00)
                  .WithValueField(0, 8, name: "BC3")
                },
                { (long)Registers.ChannelBStatus4, new ByteRegister(this, 0x00)
                  .WithValueField(0, 8, name: "BC4")
                },
                { (long)Registers.BurstPreamblePC0, new ByteRegister(this, 0x00)
                  .WithValueField(0, 8, name: "PC0")
                },
                { (long)Registers.BurstPreamblePC1, new ByteRegister(this, 0x00)
                  .WithValueField(0, 8, name: "PC1")
                },
                { (long)Registers.BurstPreamblePD0, new ByteRegister(this, 0x00)
                  .WithValueField(0, 8, name: "PD0")
                },
                { (long)Registers.BurstPreamblePD1, new ByteRegister(this, 0x00)
                  .WithValueField(0, 8, name: "PD1")
                },
            };
            return new ByteRegisterCollection(this, registersMap);
        }

        private readonly ByteRegisterCollection registers;

        private Registers lastRegister;
        private bool autoIncrement;

        private byte DevID;

        private enum Registers : byte
        {
            ChipIdVersion = 0x01,
            ClockControl = 0x02,
            ReceiverInputControl = 0x03,
            ReceiverDataControl = 0x04,
            GPOControl1 = 0x05,
            GPOControl2 = 0x06,
            SAIClockControl = 0x07,
            SRCSAOClockControl = 0x08,
            RMCKCtrlMisc=  0x09,
            DataRoutingControl= 0x0A,
            SAIDataFormat = 0x0B,
            SAO1DataFormatTDM = 0x0C,
            SAO2DataFormat = 0x0D,
            RERRUnmasking = 0x0E,
            InterruptUnmasking = 0x0F,
            InterruptMode = 0x10,
            ReceiverChannelStatus = 0x11,
            FormatDetectStatus = 0x12,
            ReceiverError = 0x13,
            InterruptStatus = 0x14,
            PLLStatus = 0x15,
            ReceiverStatus = 0x16,
            FsXTIRatio0 = 0x17,
            FsXTIRatio1 = 0x18,
            QSubcode1 = 0x19,
            QSubcode2 = 0x1A,
            QSubcode3= 0x1B,
            QSubcode4 = 0x1C,
            QSubcode5 = 0x1D,
            QSubcode6 = 0x1E,
            QSubcode7 = 0x1F,
            QSubcode8 = 0x20,
            QSubcode9 = 0x21,
            QSubcode10 = 0x22,
            ChannelAStatus0 = 0x23,
            ChannelAStatus1 = 0x24,
            ChannelAStatus2 = 0x25,
            ChannelAStatus3 = 0x26,
            ChannelAStatus4 = 0x27,
            ChannelBStatus0 = 0x28,
            ChannelBStatus1 = 0x29,
            ChannelBStatus2 = 0x2A,
            ChannelBStatus3 = 0x2B,
            ChannelBStatus4 = 0x2C,
            BurstPreamblePC0 = 0x2D,
            BurstPreamblePC1 = 0x2E,
            BurstPreamblePD0= 0x2F,
            BurstPreamblePD1= 0x30
        }
    }
}
