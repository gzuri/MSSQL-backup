# SQL Server backup
MSSQL-backup is a small console app for backup of SQL Server. Details on making the script and additional info can be found on [personal blog](http://www.gzuri.com/2015/10/mssql-backup.html "url")

## Features
 - Makes a full backup of any number of databases
 - Compress and encrypts the backups
 - Optional can upload the archive to Azure Storage


## Installation
The easiest way is just to download the compiled version from [url](https://gzuri.blob.core.windows.net/public/HorribleSubsDownload.zip "url"). Or download directly from [GitHub Release](https://github.com/gzuri/MSSQL-backup/releases/tag/1.0 "GitHub release") which should always contain the latest build, unzip it in some folder configure the destination torrent folder and you are good to go.

### Configuration
To configure the script simply open the file MSSQL-backup.exe.config with Notepad (Sublime).

    <?xml version="1.0" encoding="utf-8" ?>
    <configuration>
     <appSettings>
      <add key="BackupPath" value=""/>
      <add key="EncryptionKey" value="feX1t25LS0fAv6353FX6LcDp"/>
      <add key="IncludeDatabases" value=""/>
      <add key="AzureTableStorageConnecionString" value=""/>
      <add key="AzureBlobContainer" value=""/>
      <add key="SQLServerHostname" value=".\"/>
     </appSettings>
     <startup> 
      <supportedRuntime version="v4.0" sku=".NETFramework,Version=v4.6" />
     </startup>
    </configuration>
Settings:

 - BackupPath: Full folder path like "C:\Backups\"
 - EncryptionKey: Key for archive encryption
 - IncludeDatabases: Database names to include in backups, to add more than one database just separate the names with semicolun (ex: Db1, master) **Note: The script is case sensitive**
 -  AzureTableStorageConnecionString: self-explanatory
 -  AzureBlobContainer: self-explanatory
 -  SQLServerHostname: self-explanatory

##Additional notes
 - It's recommended to set the script in Windows Scheduler to run daily
 - Please feel free to suggest features