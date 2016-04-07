using ICSharpCode.SharpZipLib.Zip;
using Microsoft.SqlServer.Management.Smo;
using Microsoft.WindowsAzure.Storage;
using System;
using System.Collections.Generic;
using System.Configuration;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace MSSQLbackup
{
    public class Back
        { }

    public class BackupService
    {
        List<string> IGNORE_DATABASES = new List<string> { "master", "msdb", "tempdb", "model" };

        Server myServer;
        BackupSettings backupSettings;
        public BackupService(BackupSettings backupSettings)
        {
            this.backupSettings = backupSettings;

            myServer = new Server(backupSettings.DbServerHostname);
            myServer.ConnectionContext.LoginSecure = true;
            myServer.ConnectionContext.Connect();

            if (!Directory.Exists(backupSettings.LocalBackupPath))
                Directory.CreateDirectory(backupSettings.LocalBackupPath);

            if (String.IsNullOrWhiteSpace(backupSettings.LocalBackupTempPath))
                backupSettings.LocalBackupTempPath = Path.Combine(backupSettings.LocalBackupPath, "temp");

            if (!Directory.Exists(Path.Combine(backupSettings.LocalBackupTempPath)))
                Directory.CreateDirectory(backupSettings.LocalBackupTempPath);

            backupSettings.BackupDate = DateTime.Now.ToString("yyyy-mm-dd");
        }

        public List<string> GetDbNamesForBackup()
        {
            if (!String.IsNullOrEmpty(backupSettings.IncludeDatabases))
                return backupSettings.IncludeDatabases.Split(';').ToList();

            var allDatabases = new List<String>();
            foreach (Database database in myServer.Databases)
                if (IGNORE_DATABASES.All(x=>x != database.Name))
                    allDatabases.Add(database.Name);

            return allDatabases;
        }

        void CreateDatabaseBackup(string backupFilePath, string dbName)
        {
            Backup bkpDBFull = new Backup();
            /* Specify whether you want to back up database or files or log */
            bkpDBFull.Action = BackupActionType.Database;
            /* Specify the name of the database to back up */
            bkpDBFull.Database = dbName;
            //bkpDBFull.CompressionOption = BackupCompressionOptions.On;
            bkpDBFull.Devices.AddDevice(backupFilePath, DeviceType.File);
            bkpDBFull.Initialize = false;
            bkpDBFull.SqlBackup(myServer);
        }

        void ZipAndEncryptFile(string zipFileName, string originalFilePath, string encryptionKey)
        {
            FileStream fsOut = File.Create(zipFileName);
            //ZIP all databases to single file
            using (ZipOutputStream zipStream = new ZipOutputStream(fsOut))
            {
                zipStream.SetLevel(9);
                if (!String.IsNullOrWhiteSpace(encryptionKey))
                    zipStream.Password = encryptionKey;
                byte[] buffer = new byte[4096];
                
                var entry = new ZipEntry(Path.GetFileName(originalFilePath));
                if (!String.IsNullOrWhiteSpace(encryptionKey))
                    entry.AESKeySize = 256;

                zipStream.PutNextEntry(entry);

                using (FileStream fs = File.OpenRead(originalFilePath))
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
                zipStream.IsStreamOwner = true; // Makes the Close also Close the underlying stream
                zipStream.Finish();
                zipStream.Close();
            }
        }

        void ZipAndEncryptDirectory(string zipFileName, string directoryPath, string encryptionKey)
        {
            FileStream fsOut = File.Create(zipFileName);
            //ZIP all databases to single file
            using (ZipOutputStream zipStream = new ZipOutputStream(fsOut))
            {
                zipStream.SetLevel(9);
                if (!String.IsNullOrWhiteSpace(encryptionKey))
                    zipStream.Password = encryptionKey;
                byte[] buffer = new byte[4096];
                foreach (var file in Directory.GetFiles(directoryPath))
                {
                    var entry = new ZipEntry(Path.GetFileName(file));
                    if (!String.IsNullOrWhiteSpace(encryptionKey))
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

        void UploadArchiveToAzure(string fileName)
        {
            var cloudStorageAccount = CloudStorageAccount.Parse(backupSettings.AzureTableSorageConnectionString);
            var blobClient = cloudStorageAccount.CreateCloudBlobClient();
            var container = blobClient.GetContainerReference(backupSettings.AzureBlobContainer);

            var blobBlockInContainer = container.GetAppendBlobReference(Path.GetFileName(fileName));
            blobBlockInContainer.UploadFromFile(fileName, FileMode.Open);
        }

        void BackupSingleDatabase(String dbName)
        {
            try
            {
                var backupFilePath = Path.Combine(backupSettings.LocalBackupTempPath, dbName + ".bak");
                var zipFilePath = Path.Combine(backupSettings.LocalBackupPath, dbName + "-" + backupSettings.BackupDate + ".zip");

                if (File.Exists(backupFilePath))
                    File.Delete(backupFilePath);

                CreateDatabaseBackup(backupFilePath, dbName);
                ZipAndEncryptFile(zipFilePath, backupFilePath, backupSettings.EncryptionKey);

                Console.WriteLine(dbName);
                File.Delete(backupFilePath);

                if (!String.IsNullOrWhiteSpace(backupSettings.AzureTableSorageConnectionString)
                    && !String.IsNullOrWhiteSpace(backupSettings.AzureBlobContainer))
                    UploadArchiveToAzure(zipFilePath);

            }
            catch (Exception e)
            {

            }
        }

        public void DoBackup()
        {
            var databasesToBackup = GetDbNamesForBackup();

            foreach (var item in databasesToBackup)
            {
                BackupSingleDatabase(item);
            }

            if (Directory.Exists(backupSettings.LocalBackupTempPath))
                Directory.Delete(backupSettings.LocalBackupTempPath);
        }
    }
}
