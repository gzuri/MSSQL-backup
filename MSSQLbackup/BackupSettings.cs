using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace MSSQLbackup
{
    public class BackupSettings
    {
        public string DbServerHostname { get; set; }
        public string DbServerUsername { get; set; }
        public string DbServerPassword { get; set; }
        public string IncludeDatabases { get; set; }
        public string LocalBackupPath { get; set; }
        public string LocalBackupTempPath { get; set; }
        public string EncryptionKey { get; set; }
        public string AzureTableSorageConnectionString { get; set; }
        public string AzureBlobContainer { get; set; }
        //Setting for deleting old backups of the same database
        public bool DeleteOldBackups { get; set; }
        //Setting for 
        public bool CreateOnlyIfThereWereChanges { get; set; }
        public DayOfWeek? FullBackupDay { get; set; }
    }
}
