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
        String previousBackupsTempDir;
        DateTime currentDate;
        String currentDateString;
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

            previousBackupsTempDir = Path.Combine(backupSettings.LocalBackupTempPath, "previous");

            if (!Directory.Exists(previousBackupsTempDir))
                Directory.CreateDirectory(previousBackupsTempDir);

            currentDate = DateTime.Now;
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

        void ExtractZipFile(string zipFileName, string password, string outFolder)
        {
            ZipFile zf = null;
            try
            {
                FileStream fs = File.OpenRead(zipFileName);
                zf = new ZipFile(fs);
                if (!String.IsNullOrEmpty(password))
                {
                    zf.Password = password;     // AES encrypted entries are handled automatically
                }
                foreach (ZipEntry zipEntry in zf)
                {
                    if (!zipEntry.IsFile)
                    {
                        continue;           // Ignore directories
                    }
                    String entryFileName = zipEntry.Name;
                    // to remove the folder from the entry:- entryFileName = Path.GetFileName(entryFileName);
                    // Optionally match entrynames against a selection list here to skip as desired.
                    // The unpacked length is available in the zipEntry.Size property.

                    byte[] buffer = new byte[4096];     // 4K is optimum
                    Stream zipStream = zf.GetInputStream(zipEntry);

                    // Manipulate the output filename here as desired.
                    String fullZipToPath = Path.Combine(outFolder, entryFileName);
                    string directoryName = Path.GetDirectoryName(fullZipToPath);
                    if (directoryName.Length > 0)
                        Directory.CreateDirectory(directoryName);

                    // Unzip file in buffered chunks. This is just as fast as unpacking to a buffer the full size
                    // of the file, but does not waste memory.
                    // The "using" will close the stream even if an exception occurs.
                    using (FileStream streamWriter = File.Create(fullZipToPath))
                    {
                        StreamUtils.Copy(zipStream, streamWriter, buffer);
                    }
                }
            }
            finally
            {
                if (zf != null)
                {
                    zf.IsStreamOwner = true; // Makes close also shut the underlying stream
                    zf.Close(); // Ensure we release resources
                }
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
                var zipFilePath = Path.Combine(backupSettings.LocalBackupPath, dbName + "-" + currentDateString + ".zip");

                if (File.Exists(backupFilePath))
                    File.Delete(backupFilePath);

                if (File.Exists(zipFilePath))
                    File.Delete(zipFilePath);

                CreateDatabaseBackup(backupFilePath, dbName);
                var newestBackupFilePath = String.Empty;
                var previousBackups = FindOlderBackupsFileNames(dbName, zipFilePath);
                //Create backup conditionaly if creating only if something changed in the database and current date is not full backup day
                if (backupSettings.CreateOnlyIfThereWereChanges &&
                    backupSettings.FullBackupDay != currentDate.DayOfWeek)
                {
                    var lastBackup = previousBackups.FirstOrDefault(x => x.Item1 == previousBackups.Max(y => y.Item1));
                    if (lastBackup != null)
                    {
                        var lastBackupCompressedFullPath = Path.Combine(backupSettings.LocalBackupPath, lastBackup.Item2);
                        ExtractZipFile(lastBackupCompressedFullPath, backupSettings.EncryptionKey, previousBackupsTempDir);

                        var previousBackupFileFullPath = Directory.GetFiles(previousBackupsTempDir).First();
                        var lastBackupFileInfo = new FileInfo(Directory.GetFiles(previousBackupsTempDir).First());
                        var currentBackupFileInfo = new FileInfo(backupFilePath);

                        if (lastBackupFileInfo.Length != currentBackupFileInfo.Length)
                        {
                            log.InfoFormat("Archiving {0} as {1}", dbName, Path.GetFileName(zipFilePath));
                            ZipAndEncryptFile(zipFilePath, backupFilePath, backupSettings.EncryptionKey);
                            newestBackupFilePath = zipFilePath;
                        }
                        else
                        {
                            newestBackupFilePath = lastBackupCompressedFullPath;
                        }
                        File.Delete(previousBackupFileFullPath);
                    }
                    else {
                        log.InfoFormat("Archiving {0} as {1}", dbName, Path.GetFileName(zipFilePath));
                        ZipAndEncryptFile(zipFilePath, backupFilePath, backupSettings.EncryptionKey);
                        newestBackupFilePath = zipFilePath;
                    }
                }
                else {
                    log.InfoFormat("Archiving {0} as {1}", dbName, Path.GetFileName(zipFilePath));
                    ZipAndEncryptFile(zipFilePath, backupFilePath, backupSettings.EncryptionKey);
                    newestBackupFilePath = zipFilePath;
                }
                
                File.Delete(backupFilePath);

                if (backupSettings.DeleteOldBackups)
                {
                    var newestBackupFileName = Path.GetFileName(newestBackupFilePath);
                    if (backupSettings.FullBackupDay.HasValue)
                    {
                        previousBackups = previousBackups.Where(x => x.Item1.DayOfWeek != backupSettings.FullBackupDay.Value).ToList();
                    }

                    foreach (var previousBackup in previousBackups.Where(x=>x.Item2 != newestBackupFileName))
                    {
                        var previousBackupFullPath = Path.Combine(backupSettings.LocalBackupPath, previousBackup.Item2);
                        File.Delete(previousBackupFullPath);
                        log.InfoFormat("Deleted {0}", Path.GetFileName(previousBackupFullPath));
                    }
                }

                if (!String.IsNullOrWhiteSpace(backupSettings.AzureTableSorageConnectionString)
                    && !String.IsNullOrWhiteSpace(backupSettings.AzureBlobContainer))
                    UploadArchiveToAzure(zipFilePath);

            }
            catch (Exception e)
            {
                log.FatalFormat("Can't backup {0} with message {1}",dbName, e.Message);
            }
        }

        List<Tuple<DateTime, string>> FindOlderBackupsFileNames(string dbName, string currentBackup)
        {
            var currentBackupFileName = Path.GetFileName(currentBackup);
            var previousBackups = new List<string>();
            foreach (var file in Directory.GetFiles(backupSettings.LocalBackupPath))
            {
                var fileName = Path.GetFileName(file);

                if (Regex.IsMatch(Path.GetFileNameWithoutExtension(fileName), dbName + "-[0-9]{4}-[0-9]{2}-[0-9]{2}")
                    && fileName != currentBackupFileName)
                {
                    previousBackups.Add(fileName);
                }
            }

            var sortedBackupList = new List<Tuple<DateTime, string>>();
            //Order previous backups
            foreach (var item in previousBackups)
            {
                var el = Path.GetFileNameWithoutExtension(item).Split('-');
                var date = new DateTime(Convert.ToInt32(el[1]), Convert.ToInt32(el[2]), Convert.ToInt32(el[3]));
                sortedBackupList.Add(Tuple.Create(date, item));
            }
            
            return sortedBackupList;
        }

        public void DoBackup()
        {
            var databasesToBackup = GetDbNamesForBackup();

            foreach (var item in databasesToBackup)
            {
                BackupSingleDatabase(item);
            }

            foreach (var file in Directory.GetFiles(backupSettings.LocalBackupTempPath))
                File.Delete(file);

            //if (Directory.Exists(backupSettings.LocalBackupTempPath))
            //    Directory.Delete(backupSettings.LocalBackupTempPath);
        }
    }
}
