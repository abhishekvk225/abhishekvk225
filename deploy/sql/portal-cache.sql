-- Shared cache table for the Blazor portal (PortalCache:Provider=SqlServer): sessions, pending MFA sign-ins, export throttle.
-- Same shape as `dotnet sql-cache create`. Run once, in a database the portal's login can use (preferably a small database of its own).
-- Change [dbo] / [PortalCache] only together with PortalCache:SqlServer:SchemaName / TableName.
IF SCHEMA_ID(N'dbo') IS NULL EXEC(N'CREATE SCHEMA [dbo]');
IF OBJECT_ID(N'[dbo].[PortalCache]', N'U') IS NULL
BEGIN
    CREATE TABLE [dbo].[PortalCache](
        Id nvarchar(449) COLLATE SQL_Latin1_General_CP1_CS_AS NOT NULL,
        Value varbinary(MAX) NOT NULL,
        ExpiresAtTime datetimeoffset NOT NULL,
        SlidingExpirationInSeconds bigint NULL,
        AbsoluteExpiration datetimeoffset NULL,
        CONSTRAINT [pk_PortalCache_Id] PRIMARY KEY (Id));
    CREATE NONCLUSTERED INDEX [Index_PortalCache_ExpiresAtTime] ON [dbo].[PortalCache](ExpiresAtTime);
END
