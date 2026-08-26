using System;
using System.Runtime.InteropServices;
using System.Threading;
using SharpDX.XInput;

namespace RaidPad
{
    /// <summary>
    /// Native HID reader for Sony DualShock 4 / DualSense pads, used as a fallback when no XInput
    /// slot is occupied. Reads raw input reports on a background thread and exposes them as a
    /// SharpDX <see cref="Gamepad"/> so the rest of RaidPad stays XInput-shaped.
    /// </summary>
    public sealed class RaidPadHidController
    {
        private enum Family
        {
            DualShock4,
            DualSense
        }

        /// <summary>Byte offsets of each axis/button block within one input report.</summary>
        private struct Layout
        {
            public int LX;
            public int LY;
            public int RX;
            public int RY;
            public int L2;
            public int R2;
            public int Face;
            public int Shoulder;
            public int MinLen;
        }

        private const ushort SONY_VID = 0x054C;
        private static readonly ushort[] PS_PIDS = new ushort[5] { 0x05C4, 0x09CC, 0x0BA0, 0x0CE6, 0x0DF2 };

        private const uint DIGCF_PRESENT = 0x2u;
        private const uint DIGCF_DEVICEINTERFACE = 0x10u;
        private const uint GENERIC_READ = 0x80000000u;
        private const uint GENERIC_WRITE = 0x40000000u;
        private const uint FILE_SHARE_READ = 0x1u;
        private const uint FILE_SHARE_WRITE = 0x2u;
        private const uint OPEN_EXISTING = 0x3u;
        private const int HIDP_STATUS_SUCCESS = 0x110000;

        private IntPtr handle = (IntPtr)(-1);
        private Thread reader;
        private volatile bool running;
        private volatile bool alive;
        private int reportLength;
        private Family family;
        private bool firstReportLogged;

        private readonly object gate = new object();
        private readonly byte[] latest = new byte[128];
        private int latestLen;

        public bool Alive => alive;

        public bool EnsureOpen()
        {
            if (alive && handle != (IntPtr)(-1)) return true;

            Close();
            try
            {
                string path = FindDevicePath();
                if (path == null) return false;

                IntPtr h = OpenPath(path);
                if (h == (IntPtr)(-1))
                {
                    Log("found a PS pad but could not open it (in use by another app, e.g. DS4Windows/Steam?)");
                    return false;
                }

                int inputLength = GetInputReportLength(h);
                if (inputLength <= 0 || inputLength > latest.Length) inputLength = 64;
                int featureLength = GetFeatureReportLength(h);

                handle = h;
                reportLength = inputLength;
                firstReportLogged = false;
                latestLen = 0;

                Activate(h, featureLength);

                running = true;
                alive = true;
                reader = new Thread(ReaderLoop)
                {
                    IsBackground = true,
                    Name = "RaidPad-HID"
                };
                reader.Start();
                Log("native PS controller connected (" + family + ", report length " + reportLength + ")");
                return true;
            }
            catch (Exception ex)
            {
                Log("EnsureOpen failed: " + ex.Message);
                Close();
                return false;
            }
        }

        public void Close()
        {
            running = false;
            alive = false;

            IntPtr h = handle;
            handle = (IntPtr)(-1);
            if (h != (IntPtr)(-1))
            {
                try { CloseHandle(h); } catch { }
            }

            Thread t = reader;
            reader = null;
            if (t != null)
            {
                try { t.Join(200); } catch { }
            }
        }

        private void ReaderLoop()
        {
            byte[] buffer = new byte[latest.Length];
            while (running)
            {
                uint read;
                if (!ReadFile(handle, buffer, (uint)reportLength, out read, IntPtr.Zero) || read == 0)
                {
                    if (running) Log("native PS controller read failed — disconnected");
                    alive = false;
                    break;
                }
                lock (gate)
                {
                    Buffer.BlockCopy(buffer, 0, latest, 0, (int)Math.Min(read, (uint)latest.Length));
                    latestLen = (int)read;
                }
            }
        }

        public Gamepad GetGamepad()
        {
            Gamepad pad = default(Gamepad);

            int len;
            byte[] report;
            lock (gate)
            {
                if (latestLen <= 0) return pad;
                len = latestLen;
                report = new byte[len];
                Buffer.BlockCopy(latest, 0, report, 0, len);
            }

            Layout L;
            if (!ResolveLayout(report, len, out L)) return pad;

            if (!firstReportLogged)
            {
                firstReportLogged = true;
                Log("first report ok (id 0x" + report[0].ToString("X2") + ", len " + len + ", " + family + ")");
            }

            byte face = report[L.Face];
            byte shoulder = report[L.Shoulder];

            GamepadButtonFlags buttons = GamepadButtonFlags.None;
            if ((face & 0x20) != 0) buttons |= GamepadButtonFlags.A;
            if ((face & 0x40) != 0) buttons |= GamepadButtonFlags.B;
            if ((face & 0x10) != 0) buttons |= GamepadButtonFlags.X;
            if ((face & 0x80) != 0) buttons |= GamepadButtonFlags.Y;
            if ((shoulder & 0x01) != 0) buttons |= GamepadButtonFlags.LeftShoulder;
            if ((shoulder & 0x02) != 0) buttons |= GamepadButtonFlags.RightShoulder;
            if ((shoulder & 0x10) != 0) buttons |= GamepadButtonFlags.Back;
            if ((shoulder & 0x20) != 0) buttons |= GamepadButtonFlags.Start;
            if ((shoulder & 0x40) != 0) buttons |= GamepadButtonFlags.LeftThumb;
            if ((shoulder & 0x80) != 0) buttons |= GamepadButtonFlags.RightThumb;

            // Low nibble of the face byte is an 8-way hat, not a bitfield.
            switch (face & 0x0F)
            {
                case 0: buttons |= GamepadButtonFlags.DPadUp; break;
                case 1: buttons |= GamepadButtonFlags.DPadUp | GamepadButtonFlags.DPadRight; break;
                case 2: buttons |= GamepadButtonFlags.DPadRight; break;
                case 3: buttons |= GamepadButtonFlags.DPadDown | GamepadButtonFlags.DPadRight; break;
                case 4: buttons |= GamepadButtonFlags.DPadDown; break;
                case 5: buttons |= GamepadButtonFlags.DPadDown | GamepadButtonFlags.DPadLeft; break;
                case 6: buttons |= GamepadButtonFlags.DPadLeft; break;
                case 7: buttons |= GamepadButtonFlags.DPadUp | GamepadButtonFlags.DPadLeft; break;
            }

            pad.Buttons = buttons;
            pad.LeftTrigger = report[L.L2];
            pad.RightTrigger = report[L.R2];
            pad.LeftThumbX = AxisX(report[L.LX]);
            pad.LeftThumbY = AxisY(report[L.LY]);
            pad.RightThumbX = AxisX(report[L.RX]);
            pad.RightThumbY = AxisY(report[L.RY]);
            return pad;
        }

        private static short AxisX(byte v) => Clamp((v - 128) * 258);
        private static short AxisY(byte v) => Clamp(-(v - 128) * 258);

        private static short Clamp(int s)
        {
            if (s > short.MaxValue) return short.MaxValue;
            if (s < -32767) return -32767;
            return (short)s;
        }

        private bool ResolveLayout(byte[] r, int len, out Layout L)
        {
            byte reportId = r[0];

            if (family == Family.DualShock4)
            {
                // 0x11 is the extended Bluetooth report, which prefixes two extra header bytes.
                L = (reportId == 0x11)
                    ? new Layout { LX = 3, LY = 4, RX = 5, RY = 6, L2 = 10, R2 = 11, Face = 7, Shoulder = 8, MinLen = 12 }
                    : new Layout { LX = 1, LY = 2, RX = 3, RY = 4, L2 = 8, R2 = 9, Face = 5, Shoulder = 6, MinLen = 10 };
            }
            else if (reportId == 0x31)
            {
                L = new Layout { LX = 2, LY = 3, RX = 4, RY = 5, L2 = 6, R2 = 7, Face = 9, Shoulder = 10, MinLen = 11 };
            }
            else if (len >= 64)
            {
                L = new Layout { LX = 1, LY = 2, RX = 3, RY = 4, L2 = 5, R2 = 6, Face = 8, Shoulder = 9, MinLen = 10 };
            }
            else
            {
                L = new Layout { LX = 1, LY = 2, RX = 3, RY = 4, L2 = 8, R2 = 9, Face = 5, Shoulder = 6, MinLen = 10 };
            }

            return len >= L.MinLen;
        }

        /// <summary>
        /// Reading a feature report flips a Bluetooth-connected pad into full-report mode. Over USB
        /// it is already in full mode and the call is expected to be declined.
        /// </summary>
        private void Activate(IntPtr h, int featureLen)
        {
            byte featureId = (byte)((family == Family.DualSense) ? 5 : 2);
            int len = (featureLen > 0) ? featureLen : ((family == Family.DualSense) ? 41 : 37);
            if (len > 512) len = 512;

            try
            {
                byte[] buffer = new byte[len];
                buffer[0] = featureId;
                if (HidD_GetFeature(h, buffer, (uint)len))
                {
                    Log("full-report mode activated (feature 0x" + featureId.ToString("X2") + ")");
                }
                else
                {
                    Log("feature activation declined (likely USB / already full mode) — continuing");
                }
            }
            catch (Exception ex)
            {
                Log("activation failed: " + ex.Message);
            }
        }

        private string FindDevicePath()
        {
            Guid guid;
            HidD_GetHidGuid(out guid);

            IntPtr set = SetupDiGetClassDevs(ref guid, IntPtr.Zero, IntPtr.Zero, DIGCF_PRESENT | DIGCF_DEVICEINTERFACE);
            if (set == (IntPtr)(-1)) return null;

            try
            {
                SP_DEVICE_INTERFACE_DATA ifd = new SP_DEVICE_INTERFACE_DATA
                {
                    cbSize = Marshal.SizeOf(typeof(SP_DEVICE_INTERFACE_DATA))
                };

                for (uint i = 0u; SetupDiEnumDeviceInterfaces(set, IntPtr.Zero, ref guid, i, ref ifd); i++)
                {
                    string path = GetPath(set, ref ifd);
                    if (path == null) continue;

                    IntPtr h = OpenPath(path);
                    if (h == (IntPtr)(-1)) continue;

                    HIDD_ATTRIBUTES attr = new HIDD_ATTRIBUTES
                    {
                        Size = Marshal.SizeOf(typeof(HIDD_ATTRIBUTES))
                    };

                    bool match = false;
                    if (HidD_GetAttributes(h, ref attr) && attr.VendorID == SONY_VID)
                    {
                        for (int p = 0; p < PS_PIDS.Length; p++)
                        {
                            if (attr.ProductID == PS_PIDS[p])
                            {
                                family = (attr.ProductID == 0x0CE6 || attr.ProductID == 0x0DF2)
                                    ? Family.DualSense
                                    : Family.DualShock4;
                                match = true;
                                break;
                            }
                        }
                    }

                    CloseHandle(h);
                    if (match) return path;
                }
            }
            finally
            {
                SetupDiDestroyDeviceInfoList(set);
            }

            return null;
        }

        private static string GetPath(IntPtr set, ref SP_DEVICE_INTERFACE_DATA ifd)
        {
            uint requiredSize = 0u;
            SetupDiGetDeviceInterfaceDetail(set, ref ifd, IntPtr.Zero, 0u, ref requiredSize, IntPtr.Zero);
            if (requiredSize == 0) return null;

            IntPtr detail = Marshal.AllocHGlobal((int)requiredSize);
            try
            {
                // cbSize of SP_DEVICE_INTERFACE_DETAIL_DATA: 8 on x64, 6 on x86.
                Marshal.WriteInt32(detail, (IntPtr.Size == 8) ? 8 : 6);
                if (!SetupDiGetDeviceInterfaceDetail(set, ref ifd, detail, requiredSize, ref requiredSize, IntPtr.Zero))
                {
                    return null;
                }
                return Marshal.PtrToStringAuto(new IntPtr(detail.ToInt64() + 4));
            }
            finally
            {
                Marshal.FreeHGlobal(detail);
            }
        }

        private static IntPtr OpenPath(string path)
        {
            IntPtr h = CreateFile(path, GENERIC_READ | GENERIC_WRITE, FILE_SHARE_READ | FILE_SHARE_WRITE,
                IntPtr.Zero, OPEN_EXISTING, 0u, IntPtr.Zero);
            if (h == (IntPtr)(-1))
            {
                h = CreateFile(path, GENERIC_READ, FILE_SHARE_READ | FILE_SHARE_WRITE,
                    IntPtr.Zero, OPEN_EXISTING, 0u, IntPtr.Zero);
            }
            return h;
        }

        private static int GetInputReportLength(IntPtr h)
        {
            IntPtr preparsed;
            if (!HidD_GetPreparsedData(h, out preparsed)) return 0;
            try
            {
                HIDP_CAPS caps = default(HIDP_CAPS);
                if (HidP_GetCaps(preparsed, ref caps) != HIDP_STATUS_SUCCESS) return 0;
                return caps.InputReportByteLength;
            }
            finally
            {
                HidD_FreePreparsedData(preparsed);
            }
        }

        private static int GetFeatureReportLength(IntPtr h)
        {
            IntPtr preparsed;
            if (!HidD_GetPreparsedData(h, out preparsed)) return 0;
            try
            {
                HIDP_CAPS caps = default(HIDP_CAPS);
                if (HidP_GetCaps(preparsed, ref caps) != HIDP_STATUS_SUCCESS) return 0;
                return caps.FeatureReportByteLength;
            }
            finally
            {
                HidD_FreePreparsedData(preparsed);
            }
        }

        private static void Log(string msg)
        {
            try
            {
                if (RaidPadPlugin.Log != null) RaidPadPlugin.Log.LogInfo("[HID] " + msg);
            }
            catch { }
        }

        private struct HIDD_ATTRIBUTES
        {
            public int Size;
            public ushort VendorID;
            public ushort ProductID;
            public ushort VersionNumber;
        }

        private struct SP_DEVICE_INTERFACE_DATA
        {
            public int cbSize;
            public Guid InterfaceClassGuid;
            public uint Flags;
            public IntPtr Reserved;
        }

        private struct HIDP_CAPS
        {
            public ushort Usage;
            public ushort UsagePage;
            public ushort InputReportByteLength;
            public ushort OutputReportByteLength;
            public ushort FeatureReportByteLength;
            [MarshalAs(UnmanagedType.ByValArray, SizeConst = 17)]
            public ushort[] Reserved;
            public ushort NumberLinkCollectionNodes;
            public ushort NumberInputButtonCaps;
            public ushort NumberInputValueCaps;
            public ushort NumberInputDataIndices;
            public ushort NumberOutputButtonCaps;
            public ushort NumberOutputValueCaps;
            public ushort NumberOutputDataIndices;
            public ushort NumberFeatureButtonCaps;
            public ushort NumberFeatureValueCaps;
            public ushort NumberFeatureDataIndices;
        }

        [DllImport("hid.dll")]
        private static extern void HidD_GetHidGuid(out Guid guid);

        [DllImport("hid.dll", SetLastError = true)]
        private static extern bool HidD_GetAttributes(IntPtr handle, ref HIDD_ATTRIBUTES attr);

        [DllImport("hid.dll", SetLastError = true)]
        private static extern bool HidD_GetFeature(IntPtr handle, byte[] buffer, uint bufferLength);

        [DllImport("hid.dll", SetLastError = true)]
        private static extern bool HidD_GetPreparsedData(IntPtr handle, out IntPtr preparsed);

        [DllImport("hid.dll")]
        private static extern bool HidD_FreePreparsedData(IntPtr preparsed);

        [DllImport("hid.dll")]
        private static extern int HidP_GetCaps(IntPtr preparsed, ref HIDP_CAPS caps);

        [DllImport("setupapi.dll", CharSet = CharSet.Auto, SetLastError = true)]
        private static extern IntPtr SetupDiGetClassDevs(ref Guid classGuid, IntPtr enumerator, IntPtr hwndParent, uint flags);

        [DllImport("setupapi.dll", SetLastError = true)]
        private static extern bool SetupDiEnumDeviceInterfaces(IntPtr deviceInfoSet, IntPtr deviceInfoData, ref Guid interfaceClassGuid, uint memberIndex, ref SP_DEVICE_INTERFACE_DATA interfaceData);

        [DllImport("setupapi.dll", CharSet = CharSet.Auto, SetLastError = true)]
        private static extern bool SetupDiGetDeviceInterfaceDetail(IntPtr deviceInfoSet, ref SP_DEVICE_INTERFACE_DATA interfaceData, IntPtr detailData, uint detailSize, ref uint requiredSize, IntPtr deviceInfoData);

        [DllImport("setupapi.dll")]
        private static extern bool SetupDiDestroyDeviceInfoList(IntPtr deviceInfoSet);

        [DllImport("kernel32.dll", CharSet = CharSet.Auto, SetLastError = true)]
        private static extern IntPtr CreateFile(string name, uint access, uint share, IntPtr security, uint creation, uint flags, IntPtr template);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool ReadFile(IntPtr handle, byte[] buffer, uint toRead, out uint read, IntPtr overlapped);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool CloseHandle(IntPtr handle);
    }
}
