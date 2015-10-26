using ICSharpCode.SharpZipLib.Zip;
using Microsoft.SqlServer.Management.Smo;
using Microsoft.WindowsAzure.Storage;
using System;
using System.Collections.Generic;
using System.Configuration;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace MSSQLbackup
{
    class Program
    {
        static void BackupDatabase(Server myServer, string backupDestinationPath, string dbName)
        {
            Backup bkpDBFull = new Backup();
            /* Specify whether you want to back up database or files or log */
            bkpDBFull.Action = BackupActionType.Database;
            /* Specify the name of the database to back up */
            bkpDBFull.Database = dbName;

            var dbBackupPath = Path.Combine(backupDestinationPath, dbName + ".bak");
            bkpDBFull.Devices.AddDevice(dbBackupPath, DeviceType.File);
            bkpDBFull.Initialize = false;
            bkpDBFull.SqlBackup(myServer);
        }

        static void ZipAndEncryptDirectory(string zipFileName, string backupDestinationPath)
        {
            FileStream fsOut = File.Create(zipFileName);
            //ZIP all databases to single file
            using (ZipOutputStream zipStream = new ZipOutputStream(fsOut))
            {
                zipStream.SetLevel(9);
                zipStream.Password = ConfigurationManager.AppSettings.Get("EncryptionKey");
                byte[] buffer = new byte[4096];
                foreach (var file in Directory.GetFiles(backupDestinationPath))
                {
                    var entry = new ZipEntry(Path.GetFileName(file));
                    entry.AESKeySize = 256;
                    zipStream.PutNextEntry(entry);

                    using (FileStream fs = File.OpenRead(file))
                    {

                        // Using a fixed size buffer here makes no noticeable difference for output
                        // but keeps a lid on memory usage.
                        int sourceBytes;
                        do
                        {
                            sourceBytes = fs.Read(buffer, 0, buffer.Length);
                            zipStream.Write(buffer, 0, sourceBytes);
                        } while (sourceBytes > 0);
                    }

                    File.Delete(file);
                }
                zipStream.IsStreamOwner = true; // Makes the Close also Close the underlying stream
                zipStream.Finish();
                zipStream.Close();
            }
        }

        static void UploadArchiveToAzure(string fileName)
        {
            var cloudStorageAccount = CloudStorageAccount.Parse(ConfigurationManager.AppSettings.Get("AzureTableStorageConnecionString"));
            var blobClient = cloudStorageAccount.CreateCloudBlobClient();
            var container = blobClient.GetContainerReference(ConfigurationManager.AppSettings.Get("AzureBlobContainer"));

            var blobBlockInContainer = container.GetAppendBlobReference(Path.GetFileName(fileName));
            blobBlockInContainer.UploadFromFile(fileName, FileMode.Open);
        }


        static void Main(string[] args)
        {
            try
            {
                Server myServer = new Server(ConfigurationManager.AppSettings.Get("SQLServerHostname"));
                myServer.ConnectionContext.LoginSecure = true;
                myServer.ConnectionContext.Connect();

                var backupDestinationPath = Path.Combine(ConfigurationManager.AppSettings.Get("BackupPath"), DateTime.UtcNow.ToString("yyyy-MM-dd"));
                if (!Directory.Exists(backupDestinationPath))
                    Directory.CreateDirectory(backupDestinationPath);

                var databasesToBackup = ConfigurationManager.AppSettings.Get("IncludeDatabases").Split(';');
                //Backup each database
                foreach (Database myDatabase in myServer.Databases)
                {
                    if (databasesToBackup.All(x => x != myDatabase.Name))
                        continue;
                    BackupDatabase(myServer, backupDestinationPath, myDatabase.Name);
                }

                var zipFileName = Path.Combine(ConfigurationManager.AppSettings.Get("BackupPath"), DateTime.UtcNow.ToString("yyyy-MM-dd") + ".zip");
                ZipAndEncryptDirectory(zipFileName, backupDestinationPath);

                //Delete directory after use
                Directory.Delete(backupDestinationPath);

                if (!String.IsNullOrWhiteSpace(ConfigurationManager.AppSettings.Get("AzureTableStorageConnecionString")))
                {
                    UploadArchiveToAzure(zipFileName);
                }
            }
            catch 
            {
             
            }

        }
    }
}
