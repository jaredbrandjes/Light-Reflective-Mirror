using System;
using System.Collections.Generic;
using System.Text;

namespace LightReflectiveMirror
{
    public static class DataHandler
    {
        public static void WriteByte(this byte[] data, ref int position, byte value)
        {
            data[position] = value;
            position += 1;
        }

        // Every Read takes the end of the received message, not just the
        // buffer. Transports hand us a segment of a larger, reused buffer, so
        // reading up to data.Length would run on into stale bytes from earlier
        // messages instead of failing.
        static void EnsureReadable(int position, int count, int end)
        {
            if (count < 0 || position < 0 || count > end - position)
                throw new IndexOutOfRangeException($"Tried to read {count} bytes at {position}, message ends at {end}");
        }

        public static byte ReadByte(this byte[] data, ref int position, int end)
        {
            EnsureReadable(position, 1, end);
            byte value = data[position];
            position += 1;
            return value;
        }

        public static void WriteBool(this byte[] data, ref int position, bool value)
        {
            unsafe
            {
                fixed(byte* dataPtr = &data[position])
                {
                    bool* valuePtr = (bool*)dataPtr;
                    *valuePtr = value;
                    position += 1;
                }
            }
        }

        public static bool ReadBool(this byte[] data, ref int position, int end)
        {
            EnsureReadable(position, 1, end);
            bool value = BitConverter.ToBoolean(data, position);
            position += 1;
            return value;
        }

        public static void WriteString(this byte[] data, ref int position, string value)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                // Incase string is null or empty, just write nothing.
                data.WriteInt(ref position, 0);
            }
            else
            {
                data.WriteInt(ref position, value.Length);
                for (int i = 0; i < value.Length; i++)
                    data.WriteChar(ref position, value[i]);
            }
        }

        public static string ReadString(this byte[] data, ref int position, int end)
        {
            int stringSize = data.ReadInt(ref position, end);

            // The length is sent by the client. Check it against what is
            // actually left before building anything, and build the string in
            // one go: appending a char at a time is quadratic in the length.
            if (stringSize < 0 || stringSize > (end - position) / 2)
                throw new IndexOutOfRangeException($"String of {stringSize} chars at {position} runs past message end {end}");

            // WriteString sends 0 for null or empty, and this always read that back as null.
            if (stringSize == 0)
                return null;

            char[] value = new char[stringSize];
            for (int i = 0; i < stringSize; i++)
                value[i] = data.ReadChar(ref position, end);

            return new string(value);
        }

        public static void WriteBytes(this byte[] data, ref int position, byte[] value)
        {
            data.WriteInt(ref position, value.Length);
            Buffer.BlockCopy(value, 0, data, position, value.Length);
            position += value.Length;
        }

        public static byte[] ReadBytes(this byte[] data, ref int position, int end)
        {
            int byteSize = data.ReadInt(ref position, end);

            // Check before allocating: the size comes from the client.
            EnsureReadable(position, byteSize, end);

            byte[] value = new byte[byteSize];
            Buffer.BlockCopy(data, position, value, 0, byteSize);
            position += byteSize;

            return value;
        }

        public static void WriteChar(this byte[] data, ref int position, char value)
        {
            unsafe
            {
                fixed (byte* dataPtr = &data[position])
                {
                    char* valuePtr = (char*)dataPtr;
                    *valuePtr = value;
                    position += 2;
                }
            }
        }

        public static char ReadChar(this byte[] data, ref int position, int end)
        {
            EnsureReadable(position, 2, end);
            char value = BitConverter.ToChar(data, position);
            position += 2;
            return value;
        }

        public static void WriteInt(this byte[] data, ref int position, int value)
        {
            unsafe
            {
                fixed (byte* dataPtr = &data[position])
                {
                    int* valuePtr = (int*)dataPtr;
                    *valuePtr = value;
                    position += 4;
                }
            }
        }

        public static int ReadInt(this byte[] data, ref int position, int end)
        {
            EnsureReadable(position, 4, end);
            int value = BitConverter.ToInt32(data, position);
            position += 4;
            return value;
        }
    }
}
