let
  json = Json.Document(File.Contents(Path_Sample&"\config\config.json")), 
  recordList = json[ReportVisualTemplates], 
  refreshTimeAuto = Text.Format(
    "#(000021BB) #[datetime]", 
    [datetime = DateTime.LocalNow() + #duration(0, 0, 0, 0)], 
    "zh-CN"
  ), 
  result = List.Transform(
    recordList, 
    (R) =>
      Record.TransformFields(
        R, 
        {
          "value", 
          each if R[dataCategory] = "RefreshTime" and R[status] = false then refreshTimeAuto else _
        }
      )
  ), 
  record2table = Table.FromRecords(result), 
  col_type = Table.TransformColumnTypes(
    record2table, 
    {
      {"name", type text}, 
      {"description", type text}, 
      {"status", type logical}, 
      {"displayFolder", type text}, 
      {"dataCategory", type text}, 
      {"value", type text}
    }
  )
in
  col_type