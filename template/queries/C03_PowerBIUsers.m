let
    source = Json.Document(File.Contents(Path_Sample&"\config\config.json")),
    recordlist = source[PowerBIUsers],
    tablefromrecords = Table.FromRecords( recordlist ),
    userPrincipalName = Table.ExpandListColumn(tablefromrecords, "userPrincipalName"),
    roles = Table.ExpandListColumn(userPrincipalName, "roles"),
    dimension = Table.ExpandRecordColumn(roles, "roles", {"permissionName", "dimension", "value"}, {"permissionName", "dimension", "value"}),
    value = Table.ExpandListColumn(dimension, "value"),
    types = Table.TransformColumnTypes(value,{{"userID", type text}, {"userName", type text}, {"userPrincipalName", type text}, {"permissionName", type text}, {"dimension", type text}, {"value", type text}})
in
    types