let
  fxCalendar = (YearStart as number, YearEnd as number, FirstDayOfWeek as number, IsPrefix as text) as table =>
    let
      TipText = 
        if not (FirstDayOfWeek >= 0 and FirstDayOfWeek < 7) then
          #table(
            {"error"}, 
            {{"FirstDayOfWeek为 " & Text.From(FirstDayOfWeek) & ", 请输入正确数字 0 到 6 ."}}
          )
        else if YearStart > YearEnd then
          #table(
            {"error"}, 
            {{"YearStart为 " & Text.From(YearStart) & " 大于 YearEnd为 " & Text.From(YearEnd)}}
          )
        else if not List.Contains({"YES", "NO"}, IsPrefix) then
          #table({"error"}, {{"IsPrefix为 " & Text.From(IsPrefix) & " 请输入正确内容: YES | NO."}})
        else
          "输入正确", 
      yearlist = 
        let
          YearStart0 = YearStart, 
          YearEnd0   = YearEnd, 
          yearlist0  = {YearStart0, YearEnd0}
        in
          List.Buffer(yearlist0), 
      lunarColumn = 
        let
          lunarColumn0 = [
            Name = {
              "animal", 
              "day", 
              "lDate", 
              "lMonth", 
              "lunarDate", 
              "lunarMonth", 
              "lunarYear", 
              "month", 
              "year", 
              "status"
            }, 
            Type = {
              Text.Type, 
              Int64.Type, 
              Text.Type, 
              Text.Type, 
              Int64.Type, 
              Int64.Type, 
              Int64.Type, 
              Int64.Type, 
              Int64.Type, 
              Text.Type
            }, 
            Select = {"animal", "lDate", "lMonth", "lunarDate", "lunarMonth", "lunarYear", "status"}, 
            New = {
              "Animal", 
              "LunarDateCN", 
              "LunarMonthCN", 
              "LunarDate", 
              "LunarMonth", 
              "LunarYear", 
              "Status"
            }
          ]
        in
          lunarColumn0, 
      calendar1 = 
        let
          date_start = #date(yearlist{0}, 1, 1), 
          date_end = #date(yearlist{1}, 12, 31), 
          count = Duration.Days(date_end - date_start), 
          calendar0 = #table(
            type table [
              Dates = Date.Type, 
              Day = Int64.Type, 
              WeekDay = Int64.Type, 
              WeekCNS1 = Text.Type, 
              WeekCNS3 = Text.Type, 
              WeekENS1 = Text.Type, 
              WeekENS3 = Text.Type, 
              WeekEN = Text.Type, 
              WeekIndex = Int64.Type, 
              WeekNumber = Int64.Type, 
              Week = Text.Type, 
              YearWeek = Text.Type, 
              Month = Int64.Type, 
              MonthM = Text.Type, 
              MonthCN = Text.Type, 
              MonthENS3 = Text.Type, 
              MonthEN = Text.Type, 
              YearMonthM = Text.Type, 
              YearMonthUS = Text.Type, 
              YearMonth = Int64.Type, 
              Quarter = Int64.Type, 
              QuarterQ = Text.Type, 
              YearQuarterQ = Text.Type, 
              YearQuarter = Int64.Type, 
              HalfOfYearCN = Text.Type, 
              HalfOfYearEN = Text.Type, 
              YearHalf = Text.Type, 
              Year = Int64.Type, 
              FY00 = Text.Type, 
              FY = Text.Type, 
              StartOfWeek = Date.Type, 
              StartOfMonth = Date.Type, 
              StartOfQuarter = Date.Type, 
              StartOfHalfYear = Date.Type, 
              StartOfYear = Date.Type, 
              EndOfWeek = Date.Type, 
              EndOfMonth = Date.Type, 
              EndOfQuarter = Date.Type, 
              EndOfHalfYear = Date.Type, 
              EndOfYear = Date.Type, 
              DayOfWeek = Int64.Type, 
              DayOfMonth = Int64.Type, 
              DayOfQuarter = Int64.Type, 
              DayOfHalfYear = Int64.Type, 
              DayOfYear = Int64.Type, 
              DaysOfWeek = Int64.Type, 
              DaysOfMonth = Int64.Type, 
              DaysOfQuarter = Int64.Type, 
              DaysOfHalfYear = Int64.Type, 
              DaysOfYear = Int64.Type, 
              ProgressOfWeek = Percentage.Type, 
              ProgressOfMonth = Percentage.Type, 
              ProgressOfQuarter = Percentage.Type, 
              ProgressOfHalfYear = Percentage.Type, 
              ProgressOfYear = Percentage.Type
            ], 
            List.Transform(
              {0 .. count}, 
              (n) =>
                let
                  d = Date.AddDays(date_start, n)
                in
                  {
                    d, 
                    Date.Day(d), 
                    Date.DayOfWeek(d, 1), 
                    Text.End(Date.DayOfWeekName(d, "zh-CN"), 1), 
                    Date.DayOfWeekName(d, "zh-CN"), 
                    Text.Start(Date.DayOfWeekName(d, "en-US"), 1), 
                    Text.Start(Date.DayOfWeekName(d, "en-US"), 3), 
                    Date.DayOfWeekName(d, "en-US"), 
                    Number.RoundUp((Number.From(d) - 1) / 7, 0), 
                    Date.WeekOfYear(d, FirstDayOfWeek), 
                    "W" & Number.ToText(Date.WeekOfYear(d, FirstDayOfWeek), "00"), 
                    Date.ToText(d, "Yyy")
                      & "W"
                      & Number.ToText(Date.WeekOfYear(d, FirstDayOfWeek), "00"), 
                    Date.Month(d), 
                    "M" & Date.ToText(d, "MM"), 
                    Date.MonthName(d, "zh-CN"), 
                    Text.Start(Date.MonthName(d, "en-US"), 3), 
                    Date.MonthName(d, "en-US"), 
                    Date.ToText(d, "Yyy") & "M" & Date.ToText(d, "MM"), 
                    Text.Start(Date.MonthName(d, "en-US"), 3) & "-" & Date.ToText(d, "yy"), 
                    Date.Year(d) * 100 + Date.Month(d), 
                    Date.QuarterOfYear(d), 
                    "Q" & Number.ToText(Date.QuarterOfYear(d)), 
                    Date.ToText(d, "Yyy") & "Q" & Number.ToText(Date.QuarterOfYear(d)), 
                    Date.Year(d) * 100 + Date.QuarterOfYear(d), 
                    if Date.Month(d) < 7 then "上半年" else "下半年", 
                    if Date.Month(d) < 7 then "H1" else "H2", 
                    if Date.Month(d) < 7 then
                      Date.ToText(d, "Yyy") & "H1"
                    else
                      Date.ToText(d, "Yyy") & "H2", 
                    Date.Year(d), 
                    "FY" & Date.ToText(d, "yy"), 
                    "FY", 
                    Date.StartOfWeek(d), 
                    Date.StartOfMonth(d), 
                    Date.StartOfQuarter(d), 
                    if Date.Month(d) < 7 then
                      Date.StartOfYear(d)
                    else
                      Date.AddMonths(Date.StartOfYear(d), 6), 
                    Date.StartOfYear(d), 
                    Date.EndOfWeek(d), 
                    Date.EndOfMonth(d), 
                    Date.EndOfQuarter(d), 
                    if Date.Month(d) > 6 then
                      Date.EndOfYear(d)
                    else
                      Date.AddMonths(Date.EndOfYear(d), - 6), 
                    Date.EndOfYear(d), 
                    Date.DayOfWeek(d, 1) + 1, 
                    Date.Day(d), 
                    Duration.Days(d - Date.StartOfQuarter(d)) + 1, 
                    Duration.Days(
                      d
                        - (
                          if Date.Month(d) < 7 then
                            Date.StartOfYear(d)
                          else
                            Date.AddMonths(Date.StartOfYear(d), 6)
                        )
                    )
                      + 1, 
                    Date.DayOfYear(d), 
                    7, 
                    Date.Day(Date.EndOfMonth(d)), 
                    Duration.Days(Date.EndOfQuarter(d) - Date.StartOfQuarter(d)) + 1, 
                    Duration.Days(
                      (
                        if Date.Month(d) > 6 then
                          Date.EndOfYear(d)
                        else
                          Date.AddMonths(Date.EndOfYear(d), - 6)
                      )
                        - (
                          if Date.Month(d) < 7 then
                            Date.StartOfYear(d)
                          else
                            Date.AddMonths(Date.StartOfYear(d), 6)
                        )
                    )
                      + 1, 
                    Duration.Days(Date.EndOfYear(d) - Date.StartOfYear(d)) + 1, 
                    (Date.DayOfWeek(d, 1) + 1) / 7, 
                    Date.Day(d) / Date.Day(Date.EndOfMonth(d)), 
                    (Duration.Days(d - Date.StartOfQuarter(d)) + 1)
                      / (Duration.Days(Date.EndOfQuarter(d) - Date.StartOfQuarter(d)) + 1), 
                    (
                      Duration.Days(
                        d
                          - (
                            if Date.Month(d) < 7 then
                              Date.StartOfYear(d)
                            else
                              Date.AddMonths(Date.StartOfYear(d), 6)
                          )
                      )
                        + 1
                    )
                      / (
                        Duration.Days(
                          (
                            if Date.Month(d) > 6 then
                              Date.EndOfYear(d)
                            else
                              Date.AddMonths(Date.EndOfYear(d), - 6)
                          )
                            - (
                              if Date.Month(d) < 7 then
                                Date.StartOfYear(d)
                              else
                                Date.AddMonths(Date.StartOfYear(d), 6)
                            )
                        )
                          + 1
                      ), 
                    Date.DayOfYear(d) / (Duration.Days(Date.EndOfYear(d) - Date.StartOfYear(d)) + 1)
                  }
            )
          )
        in
          Table.Buffer(calendar0), 
      holidayTable = 
        let
          holiday_source = Json.Document(
            Web.Contents(
              "https://sp1.baidu.com/8aQDcjqpAAV3otqbppnN2DJv/api.php?&query=法定节假日&tn=wisetpl&format=json&resource_id=39042&oe=utf8"
            )
          ), 
          holidayList = holiday_source[data]{0}[holiday], 
          holidayTable1 = Table.FromRecords(List.Combine(List.Transform(holidayList, each _[list]))), 
          holidayTable2 = Table.TransformColumnTypes(
            holidayTable1, 
            {{"date", type date}, {"name", type text}}
          ), 
          holidayTable3 = Table.Group(
            holidayTable2, 
            {"date"}, 
            {"name", each Text.Combine([name], ","), type text}
          )
        in
          Table.Buffer(holidayTable3), 
      lunarDateTableX = (year0 as number, month0 as number) =>
        let
          lunarDate_source = Json.Document(
            Web.Contents(
              "https://sp1.baidu.com/8aQDcjqpAAV3otqbppnN2DJv/api.php?&query="
                & Text.From(year0)
                & "年"
                & Text.From(month0)
                & "月&tn=wisetpl&format=json&resource_id=39043&oe=utf8"
            )
          ), 
          almanac = lunarDate_source[data]{0}[almanac], 
          lunarDateTable3 = Table.Combine(List.Transform(almanac, each Table.FromRecords({_}))), 
          lunarDateTable1 = Table.SelectRows(
            Table.SelectColumns(lunarDateTable3, lunarColumn[Name], MissingField.UseNull), 
            each [month] = Text.From(month0)
          )
        in
          lunarDateTable1, 
      lunarDateTableN = 
        let
          YearList = List.Buffer({yearlist{0} .. yearlist{1}}), 
          MonthList = List.Buffer({1 .. 12}), 
          lunarDateList = List.Buffer(
            List.Transform(YearList, (y) => List.Transform(MonthList, (m) => lunarDateTableX(y, m)))
          ), 
          lunarDateTable = Table.Combine(List.Combine(lunarDateList)), 
          lunarDateTableType = Table.TransformColumnTypes(
            lunarDateTable, 
            List.Zip({lunarColumn[Name], lunarColumn[Type]})
          )
        in
          Table.Buffer(lunarDateTableType), 
      calendarAll = 
        let
          t1 = Table.Buffer(
            Table.NestedJoin(
              calendar1, 
              {"Dates"}, 
              holidayTable, 
              {"date"}, 
              "holiday", 
              JoinKind.LeftOuter
            )
          ), 
          t2 = Table.Buffer(Table.ExpandTableColumn(t1, "holiday", {"name"}, {"Holiday"})), 
          t3 = Table.Buffer(
            Table.NestedJoin(
              t2, 
              {"Year", "Month", "Day"}, 
              lunarDateTableN, 
              {"year", "month", "day"}, 
              "lunarDate", 
              JoinKind.LeftOuter
            )
          ), 
          t4 = Table.Buffer(
            Table.ExpandTableColumn(t3, "lunarDate", lunarColumn[Select], lunarColumn[New])
          ), 
          t5 = Table.Buffer(
            Table.ReplaceValue(
              t4, 
              each _, 
              null, 
              (X, Y, Z) =>
                if Y[Status] = "1" then
                  "假期"
                else if Y[Status] = "2" then
                  "补班"
                else if Y[Status] is null and Y[WeekDay] < 5 then
                  "工作日"
                else
                  "周末", 
              {"Status"}
            )
          ), 
          t6 = Table.Buffer(
            Table.Sort(
              Table.TransformColumnTypes(t5, {"Status", type text}), 
              {"Dates", Order.Ascending}
            )
          )
        in
          t6, 
      calendarAllNumber = 
        let
          NewNames = List.Transform(
            Table.ColumnNames(calendarAll), 
            (name) => {
              name, 
              "C"
                & Number.ToText(List.PositionOf(Table.ColumnNames(calendarAll), name)+1, "00_")
                & name
            }
          ), 
          calendarAllNumber = Table.RenameColumns(calendarAll, NewNames)
        in
          calendarAllNumber
    in
      if TipText = "输入正确" and IsPrefix = "YES" then
        calendarAllNumber
      else if TipText = "输入正确" and IsPrefix = "NO" then
        calendarAll
      else
        TipText, 
  TypeMeta0 = type function (
    YearStart as (
      type number
        meta [
          Documentation.FieldCaption = "1参: YearStart 日期表开始年份.", 
          Documentation.SampleValues = {2020}
        ]
    ), 
    YearEnd as (
      type number
        meta [
          Documentation.FieldCaption = "2参: YearEnd 日期表结束年份.", 
          Documentation.SampleValues = {2022}
        ]
    ), 
    FirstDayOfWeek as (
      type number
        meta [
          Documentation.FieldCaption  = "3参: FirstDayOfWeek 将周一至周日哪一天视为新一周的开始.", 
          Documentation.SampleValues  = {1}, 
          Documentation.AllowedValues = {0 .. 6}
        ]
    ), 
    IsPrefix as (
      type text
        meta [
          Documentation.FieldCaption  = "4参: IsPrefix 字段名称是否增加数字前置.", 
          Documentation.SampleValues  = {"YES"}, 
          Documentation.AllowedValues = {"YES", "NO"}
        ]
    )
  ) as table
    meta [
      Documentation.Name = "fxCalendar", 
      Documentation.LongDescription
        = "返回一个日期表,日期表包含农历日期信息.<ul>1参: <code>YearStart</code>  日期表开始年份 </ul> <ul>2参: <code>YearEnd</code>  日期表结束年份;</ul> <ul>3参: <code>FirstDayOfWeek</code>  将周一至周日哪一天视为一周的开始, 周日:0, 周一:1, 周二:2, 周三:3, 周四:4, 周五:5, 周六:6; </ul><ul>4参: <code>IsPrefix</code>  字段名称是否增加数字前置.</ul>", 
      Documentation.WhoAskedTheRightQuestion = "www.jiaopengzi.com", 
      Documentation.Author = "焦棚子", 
      Documentation.Examples = {
        [
          Description = "返回包含农历日期信息的日期表.", 
          Code        = "fxCalendar(2020, 2022, 1, ""YES"")", 
          Result      = "返回 2020 年到 2022 年的包含农历日期信息的日期表;周一为新一周的开始;字段名称增加数字前缀."
        ]
      }
    ]
in
  Value.ReplaceType(fxCalendar, TypeMeta0)