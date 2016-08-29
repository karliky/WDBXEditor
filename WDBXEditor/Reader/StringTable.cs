﻿using WDBXEditor.Common;
using WDBXEditor.Reader;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System;

namespace WDBXEditor.Reader
{
    public class StringTable : IDisposable
    {
        public Dictionary<int, string> Table = new Dictionary<int, string>();
        public int Size => (int)StringStream.Length;

        public MemoryStream StringStream = new MemoryStream();
        private Dictionary<string, int> _stringlookup = new Dictionary<string, int>();

        public StringTable() { }

        public StringTable(bool extended)
        {
            StringStream.WriteByte(0);
            if (extended)
                StringStream.WriteByte(0);
        }

        /// <summary>
        /// Writes the string into the stringtable and return's it's position.
        /// <para>If unqiue then it will check if the string is existent and return the previous position.</para>
        /// </summary>
        /// <param name="str"></param>
        /// <param name="unique"></param>
        /// <returns></returns>
        public int Write(string str, bool duplicates = false)
        {
            int offset = 0;
            if (str == "") //Empty string always 0
                return offset;

            //WDB2 with MaxId allows duplicates else distinct strings
            if (duplicates || !_stringlookup.TryGetValue(str, out offset))
            {
                byte[] strBytes = Encoding.UTF8.GetBytes(str);
                offset = (int)StringStream.Position;

                if (!duplicates)
                    _stringlookup.Add(str, offset);

                StringStream.Write(strBytes, 0, strBytes.Length);
                StringStream.WriteByte(0);
            }

            return offset;
        }

        public void CopyTo(Stream s)
        {
            StringStream.Position = 0;
            StringStream.CopyTo(s);
        }


        /// <summary>
        /// Reads the binary data from start to end returning a list of strings and their positions.
        /// </summary>
        /// <param name="dbReader"></param>
        /// <param name="stringTableStart"></param>
        /// <param name="stringTableEnd"></param>
        /// <returns></returns>
        public Dictionary<int, string> Read(BinaryReader dbReader, long stringTableStart, long stringTableEnd)
        {
            if (dbReader.BaseStream.Position > stringTableEnd)
                return Table;

            while (dbReader.BaseStream.Position != stringTableEnd)
            {
                int index = (int)(dbReader.BaseStream.Position - stringTableStart);
                Table[index] = dbReader.ReadStringNull(); //Extract all the strings to the string table
            }

            return Table;
        }

        public Dictionary<int, string> Read(BinaryReader dbReader, long stringTableStart)
        {
            return Read(dbReader, stringTableStart, dbReader.BaseStream.Length);
        }

        public void Dispose()
        {
            ((IDisposable)StringStream).Dispose();
        }
    }
}