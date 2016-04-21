using ICSharpCode.SharpZipLib.Core;
using ICSharpCode.SharpZipLib.Zip;
using log4net;
using Microsoft.SqlServer.Management.Smo;
using Microsoft.WindowsAzure.Storage;
using System;
using System.Collections.Generic;
using System.Configuration;
using System.IO;
using System.Linq;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;

namespace MSSQLbackup
{
    public class BackupService
    {
        ILog log = LogManager.GetLogger(typeof(BackupService));

        List<string> IGNORE_DATABASES = new List<string> { "master", "msdb", "tempdb", "model" };
        DateTime currentDate;
        String currentDateString;
        String tempBackupFolderPath;

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

            if (!String.IsNullOrWhiteSpace(backupSettings.LocalBackupTempPath))
                tempBackupFolderPath = backupSettings.LocalBackupTempPath;
            else
                tempBackupFolderPath = Path.Combine(backupSettings.LocalBackupPath, "temp");

            if (!Directory.Exists(tempBackupFolderPath))
                Directory.CreateDirectory(tempBackupFolderPath);

            currentDate = DateTime.Now.AddDays(0);
            currentDateString = currentDate.ToString("yyyy-MM-dd");

            log.InfoFormat("Backup started");
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

            var checkSum = bkpDBFull.Checksum;
            //bkpDBFull.CompressionOption = BackupCompressionOptions.On;
            bkpDBFull.Devices.AddDevice(backupFilePath, DeviceType.File);
            bkpDBFull.Initialize = false;
            bkpDBFull.SqlBackup(myServer);

            log.InfoFormat("Backup full {0}", dbName);
        }

        void ZipAndEncryptFile(string zipFileName, string originalFilePath, string encryptionKey)
        {
            if (File.Exists(zipFileName))
                File.Delete(zipFileName);

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
        
        void UploadArchiveToAzure(string fileName)
        {
            var cloudStorageAccount = CloudStorageAccount.Parse(backupSettings.AzureTableSorageConnectionString);
            var blobClient = cloudStorageAccount.CreateCloudBlobClient();
            var container = blobClient.GetContainerReference(backupSettings.AzureBlobContainer);

            var blobBlockInContainer = container.GetAppendBlobReference(Path.GetFileName(fileName));
            blobBlockInContainer.UploadFromFile(fileName, FileMode.Open);
        }

        void UploadArchiveToFtp(string dbName, string fileName)
        {
            try
            {
                FtpWebRequest ftpClient = (FtpWebRequest)WebRequest.Create(backupSettings.FtpUrl + Path.GetFileName(fileName));
                ftpClient.UseBinary = true;
                ftpClient.KeepAlive = true;
                ftpClient.Method = WebRequestMethods.Ftp.UploadFile;


                if (!String.IsNullOrWhiteSpace(backupSettings.FtpUsername))
                    ftpClient.Credentials = new NetworkCredential(backupSettings.FtpUsername, backupSettings.FtpPassword);
                else
                    ftpClient.Credentials = new NetworkCredential("anonymous", "janeDoe@contoso.com");

                
                System.IO.FileInfo fi = new System.IO.FileInfo(fileName);
                ftpClient.ContentLength = fi.Length;
                byte[] buffer = new byte[4097];
                int bytes = 0;
                int total_bytes = (int)fi.Length;
                System.IO.FileStream fs = fi.OpenRead();
                System.IO.Stream rs = ftpClient.GetRequestStream();
                while (total_bytes > 0)
                {
                    bytes = fs.Read(buffer, 0, buffer.Length);
                    rs.Write(buffer, 0, bytes);
                    total_bytes = total_bytes - bytes;
                }
                //fs.Flush();
                fs.Close();
                rs.Close();

                FtpWebResponse response = (FtpWebResponse)ftpClient.GetResponse();

                log.InfoFormat("Uploaded db {0} to FTP", dbName);

                response.Close();
            }
            catch (Exception e)
            {
                log.FatalFormat("DB {0} couldn't be uploaded to FTP because {1}", dbName, e.Message);
            }
        }

        void BackupSingleDatabase(String dbName)
        {
            try
            {
                var backupFilePath = Path.Combine(tempBackupFolderPath, dbName + ".bak");
                var zipFilePath = Path.Combine(backupSettings.LocalBackupPath, dbName + "-" + currentDateString + ".zip");
                if (!backupSettings.AddDateToArchive)
                    zipFilePath = Path.Combine(backupSettings.LocalBackupPath, dbName + ".zip");
                if (File.Exists(backupFilePath))
                    File.Delete(backupFilePath);

                if (File.Exists(zipFilePath))
                    File.Delete(zipFilePath);

                CreateDatabaseBackup(backupFilePath, dbName);

                ZipAndEncryptFile(zipFilePath, backupFilePath, backupSettings.EncryptionKey);
                log.InfoFormat("Archiving {0} as {1}", dbName, Path.GetFileName(zipFilePath));

                File.Delete(backupFilePath);

                if (!String.IsNullOrWhiteSpace(backupSettings.AzureTableSorageConnectionString)
                    && !String.IsNullOrWhiteSpace(backupSettings.AzureBlobContainer))
                    UploadArchiveToAzure(zipFilePath);

                if (!String.IsNullOrWhiteSpace(backupSettings.FtpUrl))
                    UploadArchiveToFtp(dbName, zipFilePath);

            }
            catch (FailedOperationException e)
            {
                log.InfoFormat("Database {0} is offline", dbName);
            }
            catch (Exception e)
            {
                log.FatalFormat("Can't backup {0} with message {1}", dbName, e.Message);
            }
        }
        
        public void DoBackup()
        {
            try
            {
                if (backupSettings.DeleteOldBackups)
                {
                    var files = Directory.GetFiles(backupSettings.LocalBackupPath);
                    foreach (var item in files)
                        File.Delete(item);
                }

                var databasesToBackup = GetDbNamesForBackup();
                foreach (var item in databasesToBackup)
                {
                    BackupSingleDatabase(item);
                }
            }
            catch (Exception e)
            {
                log.FatalFormat("Can't backup with message {0}", e.Message);

            }
        }
    }
}
