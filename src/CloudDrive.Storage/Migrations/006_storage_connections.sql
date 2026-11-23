-- Separate reusable NAS credentials from existing CFAPI mappings. Existing IDs/paths stay intact.
CREATE TABLE IF NOT EXISTS StorageConnections(Id TEXT PRIMARY KEY,Config TEXT NOT NULL,CredentialRef TEXT NOT NULL);
CREATE TABLE IF NOT EXISTS FolderMappings(MappingId TEXT PRIMARY KEY,StorageId TEXT NOT NULL);
CREATE INDEX IF NOT EXISTS folder_mapping_storage ON FolderMappings(StorageId);
INSERT OR IGNORE INTO StorageConnections
SELECT COALESCE(json_extract(config,'$.StorageId'),id),json_object('Id',COALESCE(json_extract(config,'$.StorageId'),id),'Server',json_extract(config,'$.Server'),'Port',json_extract(config,'$.Port'),
 'Share',json_extract(config,'$.Share'),'UserName',json_extract(config,'$.UserName'),'Domain',json_extract(config,'$.Domain')),credential_ref FROM connections;
INSERT OR IGNORE INTO FolderMappings SELECT id,COALESCE(json_extract(config,'$.StorageId'),id) FROM connections;
PRAGMA user_version=6;
