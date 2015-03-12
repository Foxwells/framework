// ZipEntry.Read.cs
// ------------------------------------------------------------------
//
// Copyright (c)  2009 Dino Chiesa
// All rights reserved.
//
// This code module is part of DotNetZip, a zipfile class library.
//
// ------------------------------------------------------------------
//
// This code is licensed under the Microsoft Public License.
// See the file License.txt for the license details.
// More info on: http://dotnetzip.codeplex.com
//
// ------------------------------------------------------------------
//
// last saved (in emacs):
// Time-stamp: <2010-January-06 14:45:12>
//
// ------------------------------------------------------------------
//
// This module defines logic for Reading the ZipEntry from a
// zip file.
//
//
// ------------------------------------------------------------------


using System;
using System.IO;

namespace PMDCP.Compression.Zip
{
    public partial class ZipEntry
    {
        private int _readExtraDepth;
        private void ReadExtraField()
        {
            _readExtraDepth++;
            // workitem 8098: ok (restore)
            long posn = this.ArchiveStream.Position;
            this.ArchiveStream.Seek(this._RelativeOffsetOfLocalHeader, SeekOrigin.Begin);
            //this._zipfile.SeekFromOrigin(this._RelativeOffsetOfLocalHeader);

            byte[] block = new byte[30];
            this.ArchiveStream.Read(block, 0, block.Length);
            int i = 26;
            Int16 filenameLength = (short)(block[i++] + block[i++] * 256);
            Int16 extraFieldLength = (short)(block[i++] + block[i++] * 256);

            // workitem 8098: ok (relative)
            this.ArchiveStream.Seek(filenameLength, SeekOrigin.Current);

            ProcessExtraField(this.ArchiveStream, extraFieldLength);

            // workitem 8098: ok (restore)
            this.ArchiveStream.Seek(posn, SeekOrigin.Begin);
            _readExtraDepth--;
        }


        private static bool ReadHeader(ZipEntry ze, System.Text.Encoding defaultEncoding)
        {
            int bytesRead = 0;

            // change for workitem 8098
            ze._RelativeOffsetOfLocalHeader = ze.ArchiveStream.Position;

            int signature = PMDCP.Compression.Zip.SharedUtilities.ReadEntrySignature(ze.ArchiveStream);
            bytesRead += 4;

            // Return false if this is not a local file header signature.
            if (ZipEntry.IsNotValidSig(signature))
            {
                // Getting "not a ZipEntry signature" is not always wrong or an error.
                // This will happen after the last entry in a zipfile.  In that case, we
                // expect to read :
                //    a ZipDirEntry signature (if a non-empty zip file) or
                //    a ZipConstants.EndOfCentralDirectorySignature.
                //
                // Anything else is a surprise.

                ze.ArchiveStream.Seek(-4, SeekOrigin.Current); // unread the signature
                if (ZipEntry.IsNotValidZipDirEntrySig(signature) && (signature != ZipConstants.EndOfCentralDirectorySignature))
                {
                    throw new BadReadException(String.Format("  ZipEntry::ReadHeader(): Bad signature (0x{0:X8}) at position  0x{1:X8}", signature, ze.ArchiveStream.Position));
                }
                return false;
            }

            byte[] block = new byte[26];
            int n = ze.ArchiveStream.Read(block, 0, block.Length);
            if (n != block.Length) return false;
            bytesRead += n;

            int i = 0;
            ze._VersionNeeded = (Int16)(block[i++] + block[i++] * 256);
            ze._BitField = (Int16)(block[i++] + block[i++] * 256);
            ze._CompressionMethod_FromZipFile = ze._CompressionMethod = (Int16)(block[i++] + block[i++] * 256);
            ze._TimeBlob = block[i++] + block[i++] * 256 + block[i++] * 256 * 256 + block[i++] * 256 * 256 * 256;
            // transform the time data into something usable (a DateTime)
            ze._LastModified = PMDCP.Compression.Zip.SharedUtilities.PackedToDateTime(ze._TimeBlob);
            ze._timestamp |= ZipEntryTimestamp.DOS;

            if ((ze._BitField & 0x01) == 0x01)
            {
                ze._Encryption_FromZipFile = ze._Encryption = EncryptionAlgorithm.PkzipWeak; // this *may* change after processing the Extra field
                ze._sourceIsEncrypted = true;
            }

            // NB: if ((ze._BitField & 0x0008) != 0x0008), then the Compressed, uncompressed and
            // CRC values are not true values; the true values will follow the entry data.
            // But, regardless of the status of bit 3 in the bitfield, the slots for
            // the three amigos may contain marker values for ZIP64.  So we must read them.
            {
                ze._Crc32 = (Int32)(block[i++] + block[i++] * 256 + block[i++] * 256 * 256 + block[i++] * 256 * 256 * 256);
                ze._CompressedSize = (uint)(block[i++] + block[i++] * 256 + block[i++] * 256 * 256 + block[i++] * 256 * 256 * 256);
                ze._UncompressedSize = (uint)(block[i++] + block[i++] * 256 + block[i++] * 256 * 256 + block[i++] * 256 * 256 * 256);

                if ((uint)ze._CompressedSize == 0xFFFFFFFF ||
                    (uint)ze._UncompressedSize == 0xFFFFFFFF)

                    ze._InputUsesZip64 = true;
            }

            Int16 filenameLength = (short)(block[i++] + block[i++] * 256);
            Int16 extraFieldLength = (short)(block[i++] + block[i++] * 256);

            block = new byte[filenameLength];
            n = ze.ArchiveStream.Read(block, 0, block.Length);
            bytesRead += n;

            // if the UTF8 bit is set for this entry, override the encoding the application requested.
            ze._actualEncoding = ((ze._BitField & 0x0800) == 0x0800)
                ? System.Text.Encoding.UTF8
                : defaultEncoding;

            // need to use this form of GetString() for .NET CF
            ze._FileNameInArchive = ze._actualEncoding.GetString(block, 0, block.Length);

            // when creating an entry by reading, the LocalFileName is the same as the FileNameInArchive
            // No, on second thought, I think it should be empty (null).
            //ze._LocalFileName = ze._FileNameInArchive;

            // workitem 6898
            if (ze._FileNameInArchive.EndsWith("/")) ze.MarkAsDirectory();

            bytesRead += ze.ProcessExtraField(ze.ArchiveStream, extraFieldLength);

            ze._LengthOfTrailer = 0;

            // workitem 6607 - don't read for directories
            // actually get the compressed size and CRC if necessary
            if (!ze._FileNameInArchive.EndsWith("/") && (ze._BitField & 0x0008) == 0x0008)
            {
                // This descriptor exists only if bit 3 of the general
                // purpose bit flag is set (see below).  It is byte aligned
                // and immediately follows the last byte of compressed data,
                // as well as any encryption trailer, as with AES.
                // This descriptor is used only when it was not possible to
                // seek in the output .ZIP file, e.g., when the output .ZIP file
                // was standard output or a non-seekable device.  For ZIP64(tm) format
                // archives, the compressed and uncompressed sizes are 8 bytes each.

                // workitem 8098: ok (restore)
                long posn = ze.ArchiveStream.Position;

                // Here, we're going to loop until we find a ZipEntryDataDescriptorSignature and
                // a consistent data record after that.   To be consistent, the data record must
                // indicate the length of the entry data.
                bool wantMore = true;
                long SizeOfDataRead = 0;
                int tries = 0;
                while (wantMore)
                {
                    tries++;
                    // We call the FindSignature shared routine to find the specified signature
                    // in the already-opened zip archive, starting from the current cursor
                    // position in that filestream.  If we cannot find the signature, then the
                    // routine returns -1, and the ReadHeader() method returns false,
                    // indicating we cannot read a legal entry header.  If we have found it,
                    // then the FindSignature() method returns the number of bytes in the
                    // stream we had to seek forward, to find the sig.  We need this to
                    // determine if the zip entry is valid, later.

                    if (ze._container.ZipFile != null)
                        ze._container.ZipFile.OnReadBytes(ze);

                    long d = PMDCP.Compression.Zip.SharedUtilities.FindSignature(ze.ArchiveStream, ZipConstants.ZipEntryDataDescriptorSignature);
                    if (d == -1) return false;

                    // total size of data read (through all loops of this).
                    SizeOfDataRead += d;

                    if (ze._InputUsesZip64)
                    {
                        // read 1x 4-byte (CRC) and 2x 8-bytes (Compressed Size, Uncompressed Size)
                        block = new byte[20];
                        n = ze.ArchiveStream.Read(block, 0, block.Length);
                        if (n != 20) return false;

                        // do not increment bytesRead - it is for entry header only.
                        // the data we have just read is a footer (falls after the file data)
                        //bytesRead += n;

                        i = 0;
                        ze._Crc32 = (Int32)(block[i++] + block[i++] * 256 + block[i++] * 256 * 256 + block[i++] * 256 * 256 * 256);
                        ze._CompressedSize = BitConverter.ToInt64(block, i);
                        i += 8;
                        ze._UncompressedSize = BitConverter.ToInt64(block, i);
                        i += 8;

                        ze._LengthOfTrailer += 24;  // bytes including sig, CRC, Comp and Uncomp sizes
                    }
                    else
                    {
                        // read 3x 4-byte fields (CRC, Compressed Size, Uncompressed Size)
                        block = new byte[12];
                        n = ze.ArchiveStream.Read(block, 0, block.Length);
                        if (n != 12) return false;

                        // do not increment bytesRead - it is for entry header only.
                        // the data we have just read is a footer (falls after the file data)
                        //bytesRead += n;

                        i = 0;
                        ze._Crc32 = (Int32)(block[i++] + block[i++] * 256 + block[i++] * 256 * 256 + block[i++] * 256 * 256 * 256);
                        ze._CompressedSize = (uint)(block[i++] + block[i++] * 256 + block[i++] * 256 * 256 + block[i++] * 256 * 256 * 256);
                        ze._UncompressedSize = (uint)(block[i++] + block[i++] * 256 + block[i++] * 256 * 256 + block[i++] * 256 * 256 * 256);

                        ze._LengthOfTrailer += 16;  // bytes including sig, CRC, Comp and Uncomp sizes

                    }

                    wantMore = (SizeOfDataRead != ze._CompressedSize);

                    if (wantMore)
                    {
                        // Seek back to un-read the last 12 bytes  - maybe THEY contain
                        // the ZipEntryDataDescriptorSignature.
                        // (12 bytes for the CRC, Comp and Uncomp size.)
                        ze.ArchiveStream.Seek(-12, SeekOrigin.Current);

                        // Adjust the size to account for the false signature read in
                        // FindSignature().
                        SizeOfDataRead += 4;
                    }
                }

                // seek back to previous position, to prepare to read file data
                // workitem 8098: ok (restore)
                ze.ArchiveStream.Seek(posn, SeekOrigin.Begin);
            }

            ze._CompressedFileDataSize = ze._CompressedSize;


            // bit 0 set indicates that some kind of encryption is in use
            if ((ze._BitField & 0x01) == 0x01)
            {
#if AESCRYPTO
                if (ze.Encryption == EncryptionAlgorithm.WinZipAes128 ||
                    ze.Encryption == EncryptionAlgorithm.WinZipAes256)
                {
                    int bits = ZipEntry.GetKeyStrengthInBits(ze._Encryption_FromZipFile);
                    // read in the WinZip AES metadata: salt + PV. 18 bytes for AES256. 10 bytes for AES128.
                    ze._aesCrypto_forExtract = WinZipAesCrypto.ReadFromStream(null, bits, ze.ArchiveStream);
                    bytesRead += ze._aesCrypto_forExtract.SizeOfEncryptionMetadata - 10; // MAC (follows crypto bytes)
                    // according to WinZip, the CompressedSize includes the AES Crypto framing data.
                    ze._CompressedFileDataSize -= ze._aesCrypto_forExtract.SizeOfEncryptionMetadata;
                    ze._LengthOfTrailer += 10;  // MAC
                }
                else
#endif
                {
                    // read in the header data for "weak" encryption
                    ze._WeakEncryptionHeader = new byte[12];
                    bytesRead += ZipEntry.ReadWeakEncryptionHeader(ze._archiveStream, ze._WeakEncryptionHeader);
                    // decrease the filedata size by 12 bytes
                    ze._CompressedFileDataSize -= 12;
                }
            }

            // Remember the size of the blob for this entry.
            // We also have the starting position in the stream for this entry.
            ze._LengthOfHeader = bytesRead;
            ze._TotalEntrySize = ze._LengthOfHeader + ze._CompressedFileDataSize + ze._LengthOfTrailer;


            // We've read in the regular entry header, the extra field, and any encryption
            // header.  The pointer in the file is now at the start of the filedata, which is
            // potentially compressed and encrypted.  Just ahead in the file, there are
            // _CompressedFileDataSize bytes of data, followed by potentially a non-zero length
            // trailer, consisting of optionally, some encryption stuff (10 byte MAC for AES),
            // and the bit-3 trailer (16 or 24 bytes).

            return true;
        }



        internal static int ReadWeakEncryptionHeader(Stream s, byte[] buffer)
        {
            // PKZIP encrypts the compressed data stream.  Encrypted files must
            // be decrypted before they can be extracted.

            // Each PKZIP-encrypted file has an extra 12 bytes stored at the start of the data
            // area defining the encryption header for that file.  The encryption header is
            // originally set to random values, and then itself encrypted, using three, 32-bit
            // keys.  The key values are initialized using the supplied encryption password.
            // After each byte is encrypted, the keys are then updated using pseudo-random
            // number generation techniques in combination with the same CRC-32 algorithm used
            // in PKZIP and implemented in the CRC32.cs module in this project.

            // read the 12-byte encryption header
            int additionalBytesRead = s.Read(buffer, 0, 12);
            if (additionalBytesRead != 12)
                throw new ZipException(String.Format("Unexpected end of data at position 0x{0:X8}", s.Position));

            return additionalBytesRead;
        }



        private static bool IsNotValidSig(int signature)
        {
            return (signature != ZipConstants.ZipEntrySignature);
        }


        /// <summary>
        /// Reads one <c>ZipEntry</c> from the given stream.  If the entry is encrypted, we don't
        /// decrypt at this point.  We also do not decompress.  Mostly we read metadata.
        /// </summary>
        /// <param name="zc">the ZipContainer this entry belongs to.</param>
        /// <param name="first">true of this is the first entry being read from the stream.</param>
        /// <returns>the <c>ZipEntry</c> read from the stream.</returns>
        internal static ZipEntry ReadEntry(ZipContainer zc, bool first)
        {
            ZipFile zf = zc.ZipFile;

            Stream s = zc.ReadStream;

            System.Text.Encoding defaultEncoding = zc.ProvisionalAlternateEncoding;
            ZipEntry entry = new ZipEntry();
            entry._Source = ZipEntrySource.ZipFile;
            entry._container = zc;
            entry._archiveStream = s;
            if (zf != null)
                zf.OnReadEntry(true, null);

            if (first) HandlePK00Prefix(s);

            // Read entry header, including any encryption header
            if (!ReadHeader(entry, defaultEncoding)) return null;

            // Store the position in the stream for this entry
            // change for workitem 8098
            entry.__FileDataPosition = entry.ArchiveStream.Position;

            // seek past the data without reading it. We will read on Extract()
            s.Seek(entry._CompressedFileDataSize + entry._LengthOfTrailer, SeekOrigin.Current);

            // ReadHeader moves the file pointer to the end of the entry header,
            // as well as any encryption header.

            // CompressedFileDataSize includes:
            //   the maybe compressed, maybe encrypted file data
            //   the encryption trailer, if any
            //   the bit 3 descriptor, if any

            // workitem 5306
            // http://www.codeplex.com/DotNetZip/WorkItem/View.aspx?WorkItemId=5306
            HandleUnexpectedDataDescriptor(entry);

            if (zf != null)
            {
                zf.OnReadBytes(entry);
                zf.OnReadEntry(false, entry);
            }

            return entry;
        }


        internal static void HandlePK00Prefix(Stream s)
        {
            // in some cases, the zip file begins with "PK00".  This is a throwback and is rare,
            // but we handle it anyway. We do not change behavior based on it.
            uint datum = (uint)PMDCP.Compression.Zip.SharedUtilities.ReadInt(s);
            if (datum != ZipConstants.PackedToRemovableMedia)
            {
                s.Seek(-4, SeekOrigin.Current); // unread the block
            }
        }



        private static void HandleUnexpectedDataDescriptor(ZipEntry entry)
        {
            Stream s = entry.ArchiveStream;

            // In some cases, the "data descriptor" is present, without a signature, even when
            // bit 3 of the BitField is NOT SET.  This is the CRC, followed
            //    by the compressed length and the uncompressed length (4 bytes for each
            //    of those three elements).  Need to check that here.
            //
            uint datum = (uint)PMDCP.Compression.Zip.SharedUtilities.ReadInt(s);
            if (datum == entry._Crc32)
            {
                int sz = PMDCP.Compression.Zip.SharedUtilities.ReadInt(s);
                if (sz == entry._CompressedSize)
                {
                    sz = PMDCP.Compression.Zip.SharedUtilities.ReadInt(s);
                    if (sz == entry._UncompressedSize)
                    {
                        // ignore everything and discard it.
                    }
                    else
                        s.Seek(-12, SeekOrigin.Current); // unread the three blocks
                }
                else
                    s.Seek(-8, SeekOrigin.Current); // unread the two blocks
            }
            else
                s.Seek(-4, SeekOrigin.Current); // unread the block

        }



        // At current cursor position in the stream, read the extra field,
        // and set the properties on the ZipEntry instance appropriately.
        // This can be called when processing the Extra field in the Central Directory,
        // or in the local header.
        internal int ProcessExtraField(Stream s, Int16 extraFieldLength)
        {
            int additionalBytesRead = 0;

            //Stream s = ArchiveStream;

            if (extraFieldLength > 0)
            {
                byte[] Buffer = this._Extra = new byte[extraFieldLength];
                additionalBytesRead = s.Read(Buffer, 0, Buffer.Length);
                long posn = s.Position - additionalBytesRead;
                int j = 0;
                while (j < Buffer.Length)
                {
                    int start = j;

                    UInt16 HeaderId = (UInt16)(Buffer[j] + Buffer[j + 1] * 256);
                    Int16 DataSize = (short)(Buffer[j + 2] + Buffer[j + 3] * 256);

                    j += 4;

                    switch (HeaderId)
                    {
                        case 0x000a:  // NTFS ctime, atime, mtime
                            j = ProcessExtraFieldWindowsTimes(Buffer, j, DataSize, posn);
                            break;

                        case 0x5455:  // Unix ctime, atime, mtime
                            j = ProcessExtraFieldUnixTimes(Buffer, j, DataSize, posn);
                            break;


                        case 0x5855:  // Info-zip Extra field (outdated)
                            // This is outdated, so the field is supported on
                            // read only.
                            j = ProcessExtraFieldInfoZipTimes(Buffer, j, DataSize, posn);
                            break;


                        case 0x0001: // ZIP64
                            j = ProcessExtraFieldZip64(Buffer, j, DataSize, posn);
                            break;

#if AESCRYPTO
                        case 0x9901: // WinZip AES encryption is in use.  (workitem 6834)
                            // we will handle this extra field only  if compressionmethod is 0x63
                            j = ProcessExtraFieldWinZipAes(Buffer, j, DataSize, posn);
                            break;
#endif
                        case 0x0017: // workitem 7968: handle PKWare Strong encryption header
                            j = ProcessExtraFieldPkwareStrongEncryption(Buffer, j);
                            break;
                    }

                    // move to the next Header in the extra field
                    j = start + DataSize + 4;
                }
            }
            return additionalBytesRead;
        }

        private int ProcessExtraFieldPkwareStrongEncryption(byte[] Buffer, int j)
        {
            //           Value     Size     Description
            //           -----     ----     -----------
            //           0x0017    2 bytes  Tag for this "extra" block type
            //           TSize     2 bytes  Size of data that follows
            //           Format    2 bytes  Format definition for this record
            //           AlgID     2 bytes  Encryption algorithm identifier
            //           Bitlen    2 bytes  Bit length of encryption key
            //           Flags     2 bytes  Processing flags
            //           CertData  TSize-8  Certificate decryption extra field data
            //                              (refer to the explanation for CertData
            //                               in the section describing the
            //                               Certificate Processing Method under
            //                               the Strong Encryption Specification)
            {
                //Int16 format = (Int16)(Buffer[j] + Buffer[j + 1] * 256);
                j += 2;
                _UnsupportedAlgorithmId = (UInt16)(Buffer[j] + Buffer[j + 1] * 256);
                j += 2;
                _Encryption_FromZipFile = _Encryption = EncryptionAlgorithm.Unsupported;

                // DotNetZip doesn't support this algorithm, but we don't need to throw here.
                // we might just be reading the archive, which is fine.  We'll need to
                // throw if Extract() is called.
            }
            return j;
        }

#if AESCRYPTO
        private int ProcessExtraFieldWinZipAes(byte[] Buffer, int j, Int16 DataSize, long posn)
        {
            if (this._CompressionMethod == 0x0063)
            {
                if ((this._BitField & 0x01) != 0x01)
                    throw new BadReadException(String.Format("  Inconsistent metadata at position 0x{0:X16}", posn));

                this._sourceIsEncrypted = true;

                //this._aesCrypto = new WinZipAesCrypto(this);
                // see spec at http://www.winzip.com/aes_info.htm
                if (DataSize != 7)
                    throw new BadReadException(String.Format("  Inconsistent WinZip AES datasize (0x{0:X4}) at position 0x{1:X16}", DataSize, posn));

                this._WinZipAesMethod = BitConverter.ToInt16(Buffer, j);
                j += 2;
                if (this._WinZipAesMethod != 0x01 && this._WinZipAesMethod != 0x02)
                    throw new BadReadException(String.Format("  Unexpected vendor version number (0x{0:X4}) for WinZip AES metadata at position 0x{1:X16}",
                        this._WinZipAesMethod, posn));

                Int16 vendorId = BitConverter.ToInt16(Buffer, j);
                j += 2;
                if (vendorId != 0x4541)
                    throw new BadReadException(String.Format("  Unexpected vendor ID (0x{0:X4}) for WinZip AES metadata at position 0x{1:X16}", vendorId, posn));

                int keystrength= -1;
                if (Buffer[j] == 1) keystrength = 128;
                if (Buffer[j] == 3) keystrength = 256;

                if (keystrength < 0)
                    throw new BadReadException(String.Format("Invalid key strength ({0})", keystrength));

                _Encryption_FromZipFile = this._Encryption = (keystrength == 128)
                    ? EncryptionAlgorithm.WinZipAes128
                    : EncryptionAlgorithm.WinZipAes256;

                j++;

                // set the actual compression method
                this._CompressionMethod_FromZipFile=
                this._CompressionMethod = BitConverter.ToInt16(Buffer, j);
                j += 2; // for the next segment of the extra field
            }
            return j;
        }

#endif


        private int ProcessExtraFieldZip64(byte[] Buffer, int j, Int16 DataSize, long posn)
        {
            // The PKWare spec says that any of {UncompressedSize, CompressedSize,
            // RelativeOffset} exceeding 0xFFFFFFFF can lead to the ZIP64 header,
            // and the ZIP64 header may contain one or more of those.  If the
            // values are present, they will be found in the prescribed order.
            // There may also be a 4-byte "disk start number."
            // This means that the DataSize must be 28 bytes or less.

            this._InputUsesZip64 = true;

            // workitem 7941: check datasize before reading.
            if (DataSize > 28)
                throw new BadReadException(String.Format("  Inconsistent datasize (0x{0:X4}) for ZIP64 extra field at position 0x{1:X16}",
                                                         DataSize, posn));
            int remainingData = DataSize;

            if (this._UncompressedSize == 0xFFFFFFFF)
            {
                if (remainingData < 8)
                    throw new BadReadException(String.Format("  Missing data for ZIP64 extra field (Uncompressed Size) at position 0x{1:X16}",
                                                             posn));

                this._UncompressedSize = BitConverter.ToInt64(Buffer, j);
                j += 8;
                remainingData -= 8;
            }
            if (this._CompressedSize == 0xFFFFFFFF)
            {
                if (remainingData < 8)
                    throw new BadReadException(String.Format("  Missing data for ZIP64 extra field (Compressed Size) at position 0x{1:X16}",
                                                             posn));

                this._CompressedSize = BitConverter.ToInt64(Buffer, j);
                j += 8;
                remainingData -= 8;
            }
            if (this._RelativeOffsetOfLocalHeader == 0xFFFFFFFF)
            {
                if (remainingData < 8)
                    throw new BadReadException(String.Format("  Missing data for ZIP64 extra field (Relative Offset) at position 0x{1:X16}",
                                                             posn));

                this._RelativeOffsetOfLocalHeader = BitConverter.ToInt64(Buffer, j);
                j += 8;
                remainingData -= 8;
            }

            // Ignore anything else. Potentially there are 4 more bytes for the
            // disk start number.  DotNetZip currently doesn't handle multi-disk
            // archives.
            return j;
        }


        private int ProcessExtraFieldInfoZipTimes(byte[] Buffer, int j, Int16 DataSize, long posn)
        {
            if (DataSize != 12 && DataSize != 8)
                throw new BadReadException(String.Format("  Unexpected datasize (0x{0:X4}) for InfoZip v1 extra field at position 0x{1:X16}", DataSize, posn));

            Int32 timet = BitConverter.ToInt32(Buffer, j);
            this._Mtime = _unixEpoch.AddSeconds(timet);
            j += 4;

            timet = BitConverter.ToInt32(Buffer, j);
            this._Atime = _unixEpoch.AddSeconds(timet);
            j += 4;

            this._Ctime = DateTime.UtcNow;

            _ntfsTimesAreSet = true;
            _timestamp |= ZipEntryTimestamp.InfoZip1; return j;
        }



        private int ProcessExtraFieldUnixTimes(byte[] Buffer, int j, Int16 DataSize, long posn)
        {
            // The Unix filetimes are 32-bit unsigned integers,
            // storing seconds since Unix epoch.
            {
                if (DataSize != 13 && DataSize != 9 && DataSize != 5)
                    throw new BadReadException(String.Format("  Unexpected datasize (0x{0:X4}) for Extended Timestamp extra field at position 0x{1:X16}", DataSize, posn));

                int remainingData = DataSize;

                if (DataSize == 13 || _readExtraDepth > 1)
                {
                    byte flag = Buffer[j++];
                    remainingData--;
                    if ((flag & 0x0001) != 0 && remainingData >= 4)
                    {
                        Int32 timet = BitConverter.ToInt32(Buffer, j);
                        this._Mtime = _unixEpoch.AddSeconds(timet);
                        j += 4;
                        remainingData -= 4;
                    }

                    if ((flag & 0x0002) != 0 && remainingData >= 4)
                    {
                        Int32 timet = BitConverter.ToInt32(Buffer, j);
                        this._Atime = _unixEpoch.AddSeconds(timet);
                        j += 4;
                        remainingData -= 4;
                    }
                    else
                        this._Atime = DateTime.UtcNow;

                    if ((flag & 0x0004) != 0 && remainingData >= 4)
                    {
                        Int32 timet = BitConverter.ToInt32(Buffer, j);
                        this._Ctime = _unixEpoch.AddSeconds(timet);
                        j += 4;
                        remainingData -= 4;
                    }
                    else
                        this._Ctime = DateTime.UtcNow;

                    _timestamp |= ZipEntryTimestamp.Unix;
                    _ntfsTimesAreSet = true;
                    _emitUnixTimes = true;
                }
                else
                    ReadExtraField(); // will recurse

            }
            return j;
        }

        private int ProcessExtraFieldWindowsTimes(byte[] Buffer, int j, Int16 DataSize, long posn)
        {
            // The NTFS filetimes are 64-bit unsigned integers, stored in Intel
            // (least significant byte first) byte order. They are expressed as the
            // number of 1.0E-07 seconds (1/10th microseconds!) past WinNT "epoch",
            // which is "01-Jan-1601 00:00:00 UTC".
            //
            // HeaderId   2 bytes    0x000a == NTFS stuff
            // Datasize   2 bytes    ?? (usually 32)
            // reserved   4 bytes    ??
            // timetag    2 bytes    0x0001 == time
            // size       2 bytes    24 == 8 bytes each for ctime, mtime, atime
            // mtime      8 bytes    win32 ticks since win32epoch
            // atime      8 bytes    win32 ticks since win32epoch
            // ctime      8 bytes    win32 ticks since win32epoch
            {
                if (DataSize != 32)
                    throw new BadReadException(String.Format("  Unexpected datasize (0x{0:X4}) for NTFS times extra field at position 0x{1:X16}", DataSize, posn));

                j += 4;  // reserved
                Int16 timetag = (Int16)(Buffer[j] + Buffer[j + 1] * 256);
                Int16 addlsize = (Int16)(Buffer[j + 2] + Buffer[j + 3] * 256);
                j += 4;  // tag and size

                if (timetag == 0x0001 && addlsize == 24)
                {
                    Int64 z = BitConverter.ToInt64(Buffer, j);
                    this._Mtime = DateTime.FromFileTimeUtc(z);
                    j += 8;

                    // At this point the library *could* set the
                    // LastModified value to coincide with the Mtime
                    // value.  In theory, they refer to the same
                    // property of the file, and should be the same
                    // anyway, allowing for differences in precision.
                    // But they are independent quantities in the zip
                    // archive, and this library will keep them separate
                    // in the object model. There is no ill effect from
                    // this, because as files are extracted, the
                    // higher-precision value (Mtime) is used if it is
                    // present.  Apps may wish to compare the Mtime
                    // versus LastModified values, but any difference
                    // when both are present is not germaine to the
                    // correctness of the library. but note: when
                    // explicitly setting either value, both are
                    // set. See the setter for LastModified or
                    // the SetNtfsTimes() method.

                    z = BitConverter.ToInt64(Buffer, j);
                    this._Atime = DateTime.FromFileTimeUtc(z);
                    j += 8;

                    z = BitConverter.ToInt64(Buffer, j);
                    this._Ctime = DateTime.FromFileTimeUtc(z);
                    j += 8;

                    _ntfsTimesAreSet = true;
                    _timestamp |= ZipEntryTimestamp.Windows;
                    _emitNtfsTimes = true;
                }
            }
            return j;
        }


    }
}
