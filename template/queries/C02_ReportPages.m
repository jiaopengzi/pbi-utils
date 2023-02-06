let
    source = Json.Document(File.Contents(Path_Sample&"\config\config.json")),
    recordlist = source[ReportPages],
    tablefromrecords = Table.FromRecords( recordlist ),
    types = Table.TransformColumnTypes(tablefromrecords,{{"ordinal", Int64.Type}, {"name", type text}, {"displayName", type text}, {"displayOption", type text}, {"height", type text}, {"width", type text}, {"verticalAlignment", type text}, {"visibility", type text}, {"pageTitleText", type text}, {"pageTitleTextColor", type text}, {"pageTitleBackgroundColor", type text}, {"navigationButtonName", type text}, {"navigationButtonDisplayName", type text}, {"navigationButtonTextColorYes", type text}, {"navigationButtonTextColorNo", type text}, {"navigationButtonBackgroundColorYes", type text}, {"navigationButtonBackgroundColorNo", type text}, {"note", type text}, {"navigationButtonTooltipYes", type text}, {"navigationButtonTooltipNo", type text}})
in
    types