using System;
using System.Runtime.InteropServices;
using SharpDX.XInput;
using UnityEngine;

namespace RaidPad
{
    /// <summary>
    /// Out-of-raid menu driver. EFT's menus are plain mouse UI, so instead of synthesising EFT
    /// input commands this drives the real Windows cursor: right stick moves, left stick scrolls,
    /// A/LT is left click, RT is right click, X rotates the held item and B sends Escape.
    /// </summary>
    public class RaidPadMenuCursor
    {
        private const uint MOUSEEVENTF_LEFTDOWN = 0x0002u;
        private const uint MOUSEEVENTF_LEFTUP = 0x0004u;
        private const uint MOUSEEVENTF_RIGHTDOWN = 0x0008u;
        private const uint MOUSEEVENTF_RIGHTUP = 0x0010u;
        private const uint MOUSEEVENTF_WHEEL = 0x0800u;

        private const uint KEYEVENTF_KEYUP = 0x0002u;
        private const uint INPUT_KEYBOARD = 0x0001u;
        private const uint MAPVK_VK_TO_VSC = 0x0000u;
        private const byte VK_ESCAPE = 27;

        private const int SM_CXSCREEN = 0;
        private const int SM_CYSCREEN = 1;

        /// <summary>One wheel notch, as defined by WHEEL_DELTA.</summary>
        private const float WHEEL_DELTA = 120f;

        private float curX;
        private float curY;
        private float scrollAccum;

        private bool prevLeft;
        private bool prevRight;
        private bool prevBack;
        private bool prevRotate;

        private bool leftHeld;
        private bool rightHeld;

        /// <summary>Raised when X / Square is pressed, so the host can rotate the dragged item.</summary>
        public Action OnRotate;

        public void Update(Gamepad gp, float dt)
        {
            if (dt <= 0f) dt = 0.016f;

            // EFT parks the cursor while a raid scene is loaded; menus need it back.
            if (Cursor.lockState != CursorLockMode.None) Cursor.lockState = CursorLockMode.None;
            if (!Cursor.visible) Cursor.visible = true;

            int screenW = GetSystemMetrics(SM_CXSCREEN);
            int screenH = GetSystemMetrics(SM_CYSCREEN);
            if (screenW <= 0) screenW = Screen.width;
            if (screenH <= 0) screenH = Screen.height;

            // Cursor movement (right stick)
            float rx = (float)gp.RightThumbX / 32767f;
            float ry = (float)gp.RightThumbY / 32767f;
            float magnitude = Mathf.Sqrt(rx * rx + ry * ry);
            float deadzone = RaidPadPlugin.RSDeadzone.Value;

            if (magnitude > deadzone)
            {
                // Rescale past the deadzone and square it, so small pushes stay precise.
                float scaled = Mathf.Clamp01((magnitude - deadzone) / (1f - deadzone));
                float speed = scaled * scaled * RaidPadPlugin.MenuCursorSpeed.Value;
                float dirX = rx / magnitude;
                float dirY = ry / magnitude;

                curX += dirX * speed * dt;
                curY -= dirY * speed * dt;
                curX = Mathf.Clamp(curX, 0f, screenW - 1f);
                curY = Mathf.Clamp(curY, 0f, screenH - 1f);
                SetCursorPos((int)curX, (int)curY);
            }
            else
            {
                // Idle: resync with wherever the mouse actually is, so a physical mouse still works.
                POINT p;
                if (GetCursorPos(out p))
                {
                    curX = p.X;
                    curY = p.Y;
                }
            }

            // Scroll (left stick Y)
            float ly = (float)gp.LeftThumbY / 32767f;
            if (Mathf.Abs(ly) > RaidPadPlugin.LSDeadzone.Value)
            {
                scrollAccum += ly * RaidPadPlugin.MenuScrollSpeed.Value * WHEEL_DELTA * dt;
                int notches = (int)(scrollAccum / WHEEL_DELTA);
                if (notches != 0)
                {
                    mouse_event(MOUSEEVENTF_WHEEL, 0u, 0u, (uint)(notches * (int)WHEEL_DELTA), UIntPtr.Zero);
                    scrollAccum -= notches * WHEEL_DELTA;
                }
            }
            else
            {
                scrollAccum = 0f;
            }

            // Left click: A or left trigger
            bool left = gp.Buttons.HasFlag(GamepadButtonFlags.A)
                        || (float)gp.LeftTrigger / 255f > RaidPadPlugin.LTDeadzone.Value;
            if (left && !prevLeft)
            {
                mouse_event(MOUSEEVENTF_LEFTDOWN, 0u, 0u, 0u, UIntPtr.Zero);
                leftHeld = true;
            }
            else if (!left && prevLeft)
            {
                mouse_event(MOUSEEVENTF_LEFTUP, 0u, 0u, 0u, UIntPtr.Zero);
                leftHeld = false;
            }
            prevLeft = left;

            // Right click: right trigger
            bool right = (float)gp.RightTrigger / 255f > RaidPadPlugin.RTDeadzone.Value;
            if (right && !prevRight)
            {
                mouse_event(MOUSEEVENTF_RIGHTDOWN, 0u, 0u, 0u, UIntPtr.Zero);
                rightHeld = true;
            }
            else if (!right && prevRight)
            {
                mouse_event(MOUSEEVENTF_RIGHTUP, 0u, 0u, 0u, UIntPtr.Zero);
                rightHeld = false;
            }
            prevRight = right;

            // Rotate held item: X / Square
            bool rotate = gp.Buttons.HasFlag(GamepadButtonFlags.X);
            if (rotate && !prevRotate) OnRotate?.Invoke();
            prevRotate = rotate;

            // Back: B / Circle
            bool back = gp.Buttons.HasFlag(GamepadButtonFlags.B);
            if (back && !prevBack) TapKey(VK_ESCAPE);
            prevBack = back;
        }

        /// <summary>Drop any held buttons. Called when the menu cursor stops owning input.</summary>
        public void Release()
        {
            if (leftHeld)
            {
                mouse_event(MOUSEEVENTF_LEFTUP, 0u, 0u, 0u, UIntPtr.Zero);
                leftHeld = false;
            }
            if (rightHeld)
            {
                mouse_event(MOUSEEVENTF_RIGHTUP, 0u, 0u, 0u, UIntPtr.Zero);
                rightHeld = false;
            }
            prevLeft = prevRight = prevBack = prevRotate = false;
            scrollAccum = 0f;
        }

        private static void SendKey(byte vk, bool up)
        {
            INPUT[] inputs = new INPUT[1];
            inputs[0].type = INPUT_KEYBOARD;
            inputs[0].u.ki = new KEYBDINPUT
            {
                wVk = vk,
                wScan = (ushort)MapVirtualKey(vk, MAPVK_VK_TO_VSC),
                dwFlags = (up ? KEYEVENTF_KEYUP : 0u),
                time = 0u,
                dwExtraInfo = IntPtr.Zero
            };
            SendInput(1u, inputs, Marshal.SizeOf(typeof(INPUT)));
        }

        private static void TapKey(byte vk)
        {
            SendKey(vk, false);
            SendKey(vk, true);
        }

        private struct POINT
        {
            public int X;
            public int Y;
        }

        private struct INPUT
        {
            public uint type;
            public InputUnion u;
        }

        [StructLayout(LayoutKind.Explicit)]
        private struct InputUnion
        {
            [FieldOffset(0)] public MOUSEINPUT mi;
            [FieldOffset(0)] public KEYBDINPUT ki;
            [FieldOffset(0)] public HARDWAREINPUT hi;
        }

        private struct KEYBDINPUT
        {
            public ushort wVk;
            public ushort wScan;
            public uint dwFlags;
            public uint time;
            public IntPtr dwExtraInfo;
        }

        private struct MOUSEINPUT
        {
            public int dx;
            public int dy;
            public uint mouseData;
            public uint dwFlags;
            public uint time;
            public IntPtr dwExtraInfo;
        }

        private struct HARDWAREINPUT
        {
            public uint uMsg;
            public ushort wParamL;
            public ushort wParamH;
        }

        [DllImport("user32.dll")]
        private static extern bool SetCursorPos(int x, int y);

        [DllImport("user32.dll")]
        private static extern bool GetCursorPos(out POINT p);

        [DllImport("user32.dll")]
        private static extern void mouse_event(uint flags, uint dx, uint dy, uint data, UIntPtr extra);

        [DllImport("user32.dll")]
        private static extern int GetSystemMetrics(int index);

        [DllImport("user32.dll", SetLastError = true)]
        private static extern uint SendInput(uint nInputs, INPUT[] pInputs, int cbSize);

        [DllImport("user32.dll")]
        private static extern uint MapVirtualKey(uint uCode, uint uMapType);
    }
}
