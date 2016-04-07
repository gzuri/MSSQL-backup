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
                    LocalBackupTempPath = ConfigurationManager.AppSettings.Get("BackupTempPath")
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
