/*
 * libchmsharp2 - a C# port of chmlib
 * Copyright (C) 2011 MindTouch, Inc.
 * www.mindtouch.com  oss@mindtouch.com
 *
 * This library is free software; you can redistribute it and/or
 * modify it under the terms of the GNU Lesser General Public
 * License as published by the Free Software Foundation; either
 * version 2.1 of the License, or (at your option) any later version.
 *
 * This library is distributed in the hope that it will be useful,
 * but WITHOUT ANY WARRANTY; without even the implied warranty of
 * MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE.  See the GNU
 * Lesser General Public License for more details.
 *
 * You should have received a copy of the GNU Lesser General Public
 * License along with this library; if not, write to the Free Software
 * Foundation, Inc., 51 Franklin Street, Fifth Floor, Boston, MA  02110-1301  USA
 */
using System.IO;
using System.Text;

namespace CHMsharp
{
    internal sealed class Storage
    {
        public static int CHM_UNCOMPRESSED = 0;
        public static int CHM_COMPRESSED = 1;

        public const string CHMU_RESET_TABLE = "::DataSpace/Storage/MSCompressed/Transform/{7FC28940-9D31-11D0-9B27-00A0C91E9C7C}/InstanceData/ResetTable";
        public const string CHMU_LZXC_CONTROLDATA = "::DataSpace/Storage/MSCompressed/ControlData";
        public const string CHMU_CONTENT = "::DataSpace/Storage/MSCompressed/Content";
        public const string CHMU_SPANINFO = "::DataSpace/Storage/MSCompressed/SpanInfo";

        public static long FetchBytes(ref ChmFileInfo h, ref byte[] buf, ulong os, long len)
        {
            long readLen = 0;
            if (h.fd == null)
                return readLen;

            h.mutex.WaitOne();
            h.fd.Seek((long)os, SeekOrigin.Begin);
            readLen = h.fd.Read(buf, 0, (int)len);
            h.mutex.ReleaseMutex();
            return readLen;
        }

        public static ulong ParseCWord(byte[] pEntry, ref uint os)
        {
            ulong accum = 0;
            byte temp;
            while ((temp = pEntry[os++]) >= 0x80)
            {
                accum <<= 7;
                accum += (ulong)(temp & 0x7f);
            }

            return (accum << 7) + temp;
        }

        public static bool ParseUTF8(byte[] pEntry, ref uint os, ulong count, ref char[] path)
        {
            byte[] res = ASCIIEncoding.Convert(Encoding.UTF8, Encoding.ASCII, pEntry, (int)os, (int)count);
            path = ASCIIEncoding.ASCII.GetChars(res);
            os += (uint)count;
            return true;
        }

        public static void SkipCWord(byte[] pEntry, ref uint os)
        {
            while (pEntry[os++] >= 0x80)
            {
            }
        }
    }
}
