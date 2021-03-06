﻿using Microsoft.WindowsAzure.Storage;
using Microsoft.WindowsAzure.Storage.Blob;
using System;
using System.Collections.Generic;
using System.Configuration;
using System.IO;
using System.Security.Cryptography;
using System.Text;

namespace LargeFileUploader
{
    class Program
    {
        const int kB = 1024;
        const int MB = kB * 1024;
        const long GB = MB * 1024;
        static int NumBytesPerChunk = 4 * MB; // A block may be up to 4 MB in size. 

        static void Main(string[] args)
        {
            if (args.Length != 1)
            {
                Console.Error.WriteLine("Bitte die Video-Datei zum Hochladen mit angeben...");
                return;
            }

            var filename = args[0];
            if (!File.Exists(filename))
            {
                Console.Error.WriteLine("Video-Datei \"{0}\" existiert nicht?", filename);
                return;
            }

            Console.WriteLine("Uploading {0}", filename);

            var connectionString = ConfigurationManager.AppSettings["storageaccount"];
            Console.WriteLine("Using connection " + connectionString);
            var storageAccount = CloudStorageAccount.Parse(connectionString);

            var containerName = ConfigurationManager.AppSettings["containername"];

            upload(new FileInfo(filename), storageAccount, containerName);
        }

        public static byte[] GetFileContent(FileInfo file, long offset, int length)
        {
            using (var stream = file.OpenRead())
            {
                stream.Seek(offset, SeekOrigin.Begin);

                byte[] contents = new byte[length];
                var len = stream.Read(contents, 0, contents.Length);
                if (len == length)
                {
                    return contents;
                }

                byte[] rest = new byte[len];
                Array.Copy(contents, rest, len);
                return rest;
            }
        }

        private static void consoleExceptionHandler(Exception ex)
        {
            Console.ResetColor();
            Console.ForegroundColor = ConsoleColor.Red;
            Console.Error.WriteLine("Problem occured, trying again. Details of the problem: ");
            for (var e = ex; e != null; e = e.InnerException)
            {
                Console.Error.WriteLine(e.Message);
            }
            Console.Error.WriteLine("---------------------------------------------------------------------");
            Console.Error.WriteLine(ex.StackTrace);
            Console.ResetColor();
            Console.Error.WriteLine("---------------------------------------------------------------------");
        }

        public static void upload(FileInfo file, CloudStorageAccount storageAccount, string containerName)
        {
            if (NumBytesPerChunk > 4 * MB) NumBytesPerChunk = 4 * MB;

            var blobName = file.Name;

            Action<Action, Action<Exception>> executeUntilSuccess = (action, exceptionHandler) =>
            {
                bool success = false;
                while (!success)
                {
                    try
                    {
                        action();
                        success = true;
                    }
                    catch (Exception ex)
                    {
                        if (exceptionHandler != null) { exceptionHandler(ex); }
                    }
                }
            };

            CloudBlobClient blobClient = storageAccount.CreateCloudBlobClient();
            CloudBlobContainer container = blobClient.GetContainerReference(containerName);
            container.CreateIfNotExists();

            var permission = container.GetPermissions();
            permission.PublicAccess = BlobContainerPublicAccessType.Container;
            container.SetPermissions(permission);

            CloudBlockBlob blob = container.GetBlockBlobReference(blobName);

            List<string> blockIdList = new List<string>();
            List<string> debugList = new List<string>();

            Action saveListToFile = () =>
            {
                var text = new StringBuilder();
                text.AppendLine("{");
                text.AppendLine("  file = \"" + file.FullName + "\", ");
                text.AppendLine("  length = " + file.Length + ", ");
                text.AppendLine("  chunkSize = " + NumBytesPerChunk + ", ");
                text.AppendLine("  blobname = \"" + blobName + "\", ");
                text.AppendLine("  blobEndpoint = \"" + storageAccount.BlobEndpoint.AbsoluteUri + "\", ");
                text.AppendLine("  chunks =");
                text.AppendLine("  {");
                debugList.ForEach(_ => text.AppendLine(_));
                text.AppendLine("  }");
                text.AppendLine("}");

                File.WriteAllText("log.txt", text.ToString());
            };

            Console.WriteLine("Starting upload in chunks of {0} bytes", NumBytesPerChunk);

            MD5 hashFunction = MD5.Create();
            Func<byte[], string> md5 = (content) => Convert.ToBase64String(hashFunction.ComputeHash(content));

            DateTime initialStartTime = DateTime.UtcNow;
            int id = 0;
            for (long index = 0; index < file.Length; index += NumBytesPerChunk, id++)
            {
                byte[] blockData = GetFileContent(file, index, NumBytesPerChunk);
                string contentHash = md5(blockData);

                string blockId = Convert.ToBase64String(System.BitConverter.GetBytes(id));

                //string plaintextBlockId = string.Format("{{ filename = \"{0}\", segment = {1}, startByte = {2}, endByte = {3}, md5 = \"{4}\" }}",
                //    file.FullName, id, index, index + blockData.Length, contentHash);
                //string blockId = Convert.ToBase64String(Encoding.UTF8.GetBytes(plaintextBlockId));

                DateTime start = DateTime.UtcNow;

                executeUntilSuccess(() => blob.PutBlock(blockId, new MemoryStream(blockData, true), contentHash), consoleExceptionHandler);
                blockIdList.Add(blockId);
                debugList.Add(string.Format("      {{ blockId = {0}, firstByte = {1}, lastByte = {2}, md5 = \"{3}\" }}, ",
                    blockId, index, index + blockData.Length - 1, contentHash));
                executeUntilSuccess(() => saveListToFile(), consoleExceptionHandler);

                var kbPerSec = (((double)blockData.Length) / (DateTime.UtcNow.Subtract(start).TotalSeconds * kB));
                var MBPerMin = (((double)blockData.Length) / (DateTime.UtcNow.Subtract(start).TotalMinutes * MB));

                Func<long, long, string> absoluteProgress = (current, total) =>
                {
                    if (file.Length < kB)
                    {
                        // Bytes is reasonable
                        return string.Format("{0} of {1} bytes", current, total);
                    }
                    else if (file.Length < 10 * MB)
                    {
                        // kB is a reasonable unit
                        return string.Format("{0} of {1} kByte", (current / kB), (total / kB));
                    }
                    else if (file.Length < 10 * GB)
                    {
                        // MB is a reasonable unit
                        return string.Format("{0} of {1} MB", (current / MB), (total / MB));
                    }
                    else
                    {
                        // GB is a reasonable unit
                        return string.Format("{0} of {1} GB", (current / GB), (total / GB));
                    }
                };

                Func<long, long, string> relativeProgress = (current, total) => string.Format(
                    "{0} %", (100.0 * current / total).ToString("F3"));

                Func<long, long, string> estimatedArrivalTime = (current, total) =>
                {
                    double elapsedSeconds = DateTime.UtcNow.Subtract(initialStartTime).TotalSeconds;
                    double progress = ((double)current) / ((double)total);

                    if (current == 0) return "unknown time";

                    double remainingSeconds = elapsedSeconds * (1 - progress) / progress;

                    TimeSpan remaining = TimeSpan.FromSeconds(remainingSeconds);

                    return string.Format("{0} remaining, (expect to finish by {1} local time)",
                        remaining.ToString("g"), 
                        DateTime.Now.ToLocalTime().Add(remaining));
                };

                Console.WriteLine(
                    "Uploaded {0} ({1}) with {2} kB/sec ({3} MB/min), {4}",
                    absoluteProgress(index + blockData.Length, file.Length),
                    relativeProgress(index + blockData.Length, file.Length),
                    kbPerSec.ToString("F0"),
                    MBPerMin.ToString("F1"),
                    estimatedArrivalTime(index + blockData.Length, file.Length));
            }

            executeUntilSuccess(() => blob.PutBlockList(blockIdList), consoleExceptionHandler);
            executeUntilSuccess(() => saveListToFile(), consoleExceptionHandler);
            Console.WriteLine("PutBlockList succeeded, finished upload to {0}", blob.Uri.AbsoluteUri);
        }
    }
}