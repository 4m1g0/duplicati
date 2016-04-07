﻿//  Copyright (C) 2015, The Duplicati Team
//  http://www.duplicati.com, info@duplicati.com
//
//  This library is free software; you can redistribute it and/or modify
//  it under the terms of the GNU Lesser General Public License as
//  published by the Free Software Foundation; either version 2.1 of the
//  License, or (at your option) any later version.
//
//  This library is distributed in the hope that it will be useful, but
//  WITHOUT ANY WARRANTY; without even the implied warranty of
//  MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE. See the GNU
//  Lesser General Public License for more details.
//
//  You should have received a copy of the GNU Lesser General Public
//  License along with this library; if not, write to the Free Software
//  Foundation, Inc., 59 Temple Place, Suite 330, Boston, MA 02111-1307 USA
using System;
using CoCoL;
using System.Threading.Tasks;
using Duplicati.Library.Interface;
using System.Collections.Generic;
using Duplicati.Library.Main.Operation.Common;

namespace Duplicati.Library.Main.Operation.Backup
{
    /// <summary>
    /// This process takes files that are processed for metadata, 
    /// and checks if anything indicates that the file has changed
    /// and submits potentially changed files for scanning
    /// </summary>
    internal static class FilePreFilterProcess
    {
        public static Task Run(Snapshots.ISnapshotService snapshot, Options options, BackupStatsCollector stats, BackupDatabase database)
        {
            return AutomationExtensions.RunTask(
            new
            {
                LogChannel = Common.Channels.LogChannel.ForWrite,
                Input = Channels.ProcessedFiles.ForRead,
                Output = Channels.AcceptedChangedFile.ForWrite
            },

            async self =>
            {

                var EMPTY_METADATA = Utility.WrapMetadata(new Dictionary<string, string>(), options);
                var blocksize = options.Blocksize;
                var log = new LogWrapper(self.LogChannel);

                Console.WriteLine("Starting pre-processor");

                try
                {
                    while (true)
                    {
                        var e = await self.Input.ReadAsync();

                        Console.WriteLine("Processing file: {0}", e.Path);

                        long filestatsize = -1;
                        try
                        {
                            filestatsize = snapshot.GetFileSize(e.Path);
                        }
                        catch
                        {
                        }

                        await stats.AddExaminedFile(filestatsize);

                        e.MetaHashAndSize = options.StoreMetadata ? Utility.WrapMetadata(await MetadataGenerator.GenerateMetadataAsync(e.Path, e.Attributes, options, snapshot, log), options) : EMPTY_METADATA;

                        var timestampChanged = e.LastWrite != e.OldModified || e.LastWrite.Ticks == 0 || e.OldModified.Ticks == 0;
                        var filesizeChanged = filestatsize < 0 || e.LastFileSize < 0 || filestatsize != e.LastFileSize;
                        var tooLargeFile = options.SkipFilesLargerThan != long.MaxValue && options.SkipFilesLargerThan != 0 && filestatsize >= 0 && filestatsize > options.SkipFilesLargerThan;
                        e.MetadataChanged = !options.SkipMetadata && (e.MetaHashAndSize.Size != e.OldMetaSize || e.MetaHashAndSize.Hash != e.OldMetaHash);

                        if ((e.OldId < 0 || options.DisableFiletimeCheck || timestampChanged || filesizeChanged || e.MetadataChanged) && !tooLargeFile)
                        {
                            await log.WriteVerboseAsync("Checking file for changes {0}, new: {1}, timestamp changed: {2}, size changed: {3}, metadatachanged: {4}, {5} vs {6}", e.Path, e.OldId <= 0, timestampChanged, filesizeChanged, e.MetadataChanged, e.LastWrite, e.OldModified);
                            await self.Output.WriteAsync(e);
                        }
                        else
                        {
                            if (options.SkipFilesLargerThan == long.MaxValue || options.SkipFilesLargerThan == 0 || snapshot.GetFileSize(e.Path) < options.SkipFilesLargerThan)
                                await log.WriteVerboseAsync("Skipped checking file, because timestamp was not updated {0}", e.Path);
                            else
                                await log.WriteVerboseAsync("Skipped checking file, because the size exceeds limit {0}", e.Path);

                            await database.AddUnmodifiedAsync(e.OldId, e.LastWrite);
                        }
                    }

                }
                catch(RetiredException)
                {
                    Console.WriteLine("Done in pre-processor");
                    throw;
                }
                finally
                {
                    Console.WriteLine("Quit pre-processor");
                }
            });
        }
    }
}

