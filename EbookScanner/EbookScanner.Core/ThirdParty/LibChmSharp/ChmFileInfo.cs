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
using System.Threading;

namespace CHMsharp
{
    internal struct ChmFileInfo
    {
        public FileStream fd;
        public Mutex mutex;
        public Mutex lzx_mutex;
        public Mutex cache_mutex;
        public ulong dir_offset;
        public ulong dir_len;
        public ulong data_offset;
        public int index_root;
        public int index_head;
        public uint block_len;
        public ulong span;
        public ChmUnitInfo rt_unit;
        public ChmUnitInfo cn_unit;
        public chmLzxcResetTable reset_table;
        public bool compression_enabled;
        public uint window_size;
        public uint reset_interval;
        public uint reset_blkcount;
        public bool lzx_init;
        public Lzx.LZXstate lzx_state;
        public int lzx_last_block;
        public byte[][] cache_blocks;
        public ulong[] cache_block_indices;
        public int cache_num_blocks;
    }
}
