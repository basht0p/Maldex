-- Idempotent database bootstrap, run by the db-init service as the admin login.
-- Values come from sqlcmd scripting variables, which sqlcmd reads from the environment:
--   MALDEX_DB_NAME, MALDEX_DB_APP_USER, MALDEX_DB_APP_PASSWORD
-- Schema changes are NOT made here; the migrate service applies EF Core migrations.
SET NOCOUNT ON;

IF DB_ID(N'$(MALDEX_DB_NAME)') IS NULL
BEGIN
    PRINT 'Creating database $(MALDEX_DB_NAME)';
    CREATE DATABASE [$(MALDEX_DB_NAME)];
END
GO

-- Least-privilege runtime login: read/write data, no DDL.
IF NOT EXISTS (SELECT 1 FROM sys.server_principals WHERE name = N'$(MALDEX_DB_APP_USER)')
BEGIN
    PRINT 'Creating login $(MALDEX_DB_APP_USER)';
    CREATE LOGIN [$(MALDEX_DB_APP_USER)] WITH PASSWORD = N'$(MALDEX_DB_APP_PASSWORD)', CHECK_POLICY = ON;
END
ELSE
BEGIN
    -- Keeps the login in step with deploy/.env when the password is rotated there.
    ALTER LOGIN [$(MALDEX_DB_APP_USER)] WITH PASSWORD = N'$(MALDEX_DB_APP_PASSWORD)';
END
GO

USE [$(MALDEX_DB_NAME)];
GO

IF NOT EXISTS (SELECT 1 FROM sys.database_principals WHERE name = N'$(MALDEX_DB_APP_USER)')
    CREATE USER [$(MALDEX_DB_APP_USER)] FOR LOGIN [$(MALDEX_DB_APP_USER)];
ELSE
    -- Re-map after a restore onto a different server, where the login has a new SID.
    ALTER USER [$(MALDEX_DB_APP_USER)] WITH LOGIN = [$(MALDEX_DB_APP_USER)];

ALTER ROLE db_datareader ADD MEMBER [$(MALDEX_DB_APP_USER)];
ALTER ROLE db_datawriter ADD MEMBER [$(MALDEX_DB_APP_USER)];
GRANT EXECUTE TO [$(MALDEX_DB_APP_USER)];
GO
