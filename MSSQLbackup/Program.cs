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
        static void Main(string[] args)
        {
            try
            {
                var backupSettings = new BackupSettings
                {
                    DbServerHostname = ConfigurationManager.AppSettings.Get("SQLServerHostname"),
                    LocalBackupPath = ConfigurationManager.AppSettings.Get("BackupPath"),
                    EncryptionKey = ConfigurationManager.AppSettings.Get("EncryptionKey"),
                    LocalBackupTempPath = ConfigurationManager.AppSettings.Get("BackupTempPath"),
                    DeleteOldBackups = Convert.ToBoolean(ConfigurationManager.AppSettings.Get("DeleteOldBackups")),
                    AddDateToArchive = Convert.ToBoolean(ConfigurationManager.AppSettings.Get("AddDateToArchive")),
                    FtpUrl = ConfigurationManager.AppSettings.Get("FtpUrl"),
                    FtpUsername = ConfigurationManager.AppSettings.Get("FtpUsername"),
                    FtpPassword = ConfigurationManager.AppSettings.Get("FtpPassword")
                };

                var backupService = new BackupService(backupSettings);

                backupService.DoBackup();
            }
            catch (Exception e)
            {
             
            }

        }
    }
}
