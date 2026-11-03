// FilePath    : pbi-utils\src\PbiUtils.Application\Calendar\TimeIntelligence\ScaffoldPeriodDefinitions.cs
// Author      : jiaopengzi
// Blog        : https://jiaopengzi.com
// Copyright   : Copyright (c) 2026 by jiaopengzi, All Rights Reserved.
// Description : 保存本地51项参考期间的声明式定义, 不执行参考CSX或删除已有对象.

namespace PbiUtils.Application.Calendar.TimeIntelligence;

/// <summary>引用与公式作为数据保存, 依赖闭包由共享生成器完成.</summary>
internal static class ScaffoldPeriodDefinitions
{
    /// <summary>原始序号用于稳定名称, 依赖均为公开模板标识.</summary>
    internal sealed record Definition(int Ordinal, ScaffoldPeriodTemplate Template, string Expression);

    internal static readonly Definition[] All =
    [
        new(0, new("AC", "Day", "AC", "AC：当前选择日期对应的值。", []),
            """
            VAR CurrentDateEnd =
                LASTDATE ( {{DATE}} )

            VAR Result =
                CALCULATE (
                    {{MEASURE}},
                    CurrentDateEnd
                )

            RETURN
                Result
            """),
        new(1, new("PD", "Day", "PD", "PD：当前选择日期的前一日的值。", []),
            """
            VAR CurrentDateEnd =
                LASTDATE ( {{DATE}} )

            VAR PreviousDateEnd =
                DATEADD (
                    CurrentDateEnd,
                    -1,
                    DAY
                )

            VAR Result =
                CALCULATE (
                    {{MEASURE}},
                    PreviousDateEnd
                )

            RETURN
                Result
            """),
        new(2, new("ΔPD", "Day", "ΔPD", "ΔPD = AC-PD。", ["AC", "PD"]),
            """
            VAR CurrentDateEnd =
                LASTDATE ( {{DATE}} )

            VAR PreviousDateEnd =
                DATEADD (
                    CurrentDateEnd,
                    -1,
                    DAY
                )

            VAR CurrentValue =
                CALCULATE (
                    {{MEASURE}},
                    CurrentDateEnd
                )

            VAR PreviousDayValue =
                CALCULATE (
                    {{MEASURE}},
                    PreviousDateEnd
                )

            VAR Result =
                CurrentValue - PreviousDayValue

            RETURN
                Result
            """),
        new(3, new("DOD%", "Day", "DOD%", "DOD% = ( AC - PD ) / PD * 100%，即日环比。", ["AC", "PD"]),
            """
            VAR CurrentDateEnd =
                LASTDATE ( {{DATE}} )

            VAR PreviousDateEnd =
                DATEADD (
                    CurrentDateEnd,
                    -1,
                    DAY
                )

            VAR CurrentValue =
                CALCULATE (
                    {{MEASURE}},
                    CurrentDateEnd
                )

            VAR PreviousDayValue =
                CALCULATE (
                    {{MEASURE}},
                    PreviousDateEnd
                )

            VAR Result =
                DIVIDE (
                    CurrentValue - PreviousDayValue,
                    PreviousDayValue,
                    BLANK ()
                )

            RETURN
                Result
            """),
        new(4, new("PY", "Day", "PY", "PY：当前选择日期的前一年相同日期的值。", []),
            """
            VAR CurrentDateEnd =
                LASTDATE ( {{DATE}} )

            VAR PreviousDateEnd =
                DATEADD (
                    CurrentDateEnd,
                    -1,
                    YEAR
                )

            VAR Result =
                CALCULATE (
                    {{MEASURE}},
                    PreviousDateEnd
                )

            RETURN
                Result
            """),
        new(5, new("ΔPY", "Day", "ΔPY", "ΔPY = AC - PY。", ["AC", "PY"]),
            """
            VAR CurrentDateEnd =
                LASTDATE ( {{DATE}} )

            VAR PreviousDateEnd =
                DATEADD (
                    CurrentDateEnd,
                    -1,
                    YEAR
                )

            VAR CurrentValue =
                CALCULATE (
                    {{MEASURE}},
                    CurrentDateEnd
                )

            VAR PreviousYearValue =
                CALCULATE (
                    {{MEASURE}},
                    PreviousDateEnd
                )

            VAR Result =
                CurrentValue - PreviousYearValue

            RETURN
                Result
            """),
        new(6, new("YOY%", "Day", "YOY%", "YOY% = ( AC - PY ) / PY * 100%，即同比去年。", ["AC", "PY"]),
            """
            VAR CurrentDateEnd =
                LASTDATE ( {{DATE}} )

            VAR PreviousDateEnd =
                DATEADD (
                    CurrentDateEnd,
                    -1,
                    YEAR
                )

            VAR CurrentValue =
                CALCULATE (
                    {{MEASURE}},
                    CurrentDateEnd
                )

            VAR PreviousYearValue =
                CALCULATE (
                    {{MEASURE}},
                    PreviousDateEnd
                )

            VAR Result =
                DIVIDE (
                    CurrentValue - PreviousYearValue,
                    PreviousYearValue,
                    BLANK ()
                )

            RETURN
                Result
            """),
        new(7, new("PW", "Week", "PW", "PW：当前所选日期对应周几的上一周周几，例如：当前选择是周一，PW 表示上周一。", []),
            """
            VAR CurrentDateEnd =
                LASTDATE ( {{DATE}} )

            VAR PreviousDateEnd =
                DATEADD (
                    CurrentDateEnd,
                    -7,
                    DAY
                )

            VAR Result =
                CALCULATE (
                    {{MEASURE}},
                    PreviousDateEnd
                )

            RETURN
                Result
            """),
        new(8, new("ΔPW", "Week", "ΔPW", "ΔPW = AC-PW。", ["AC", "PW"]),
            """
            VAR CurrentDateEnd =
                LASTDATE ( {{DATE}} )

            VAR PreviousDateEnd =
                DATEADD (
                    CurrentDateEnd,
                    -7,
                    DAY
                )

            VAR CurrentValue =
                CALCULATE (
                    {{MEASURE}},
                    CurrentDateEnd
                )

            VAR PreviousWeekValue =
                CALCULATE (
                    {{MEASURE}},
                    PreviousDateEnd
                )

            VAR Result =
                CurrentValue - PreviousWeekValue

            RETURN
                Result
            """),
        new(9, new("WOW%", "Week", "WOW%", "WOW% = ( AC - PW ) / PW * 100%，即周环比。", ["AC", "PW"]),
            """
            VAR CurrentDateEnd =
                LASTDATE ( {{DATE}} )

            VAR PreviousDateEnd =
                DATEADD (
                    CurrentDateEnd,
                    -7,
                    DAY
                )

            VAR CurrentValue =
                CALCULATE (
                    {{MEASURE}},
                    CurrentDateEnd
                )

            VAR PreviousWeekValue =
                CALCULATE (
                    {{MEASURE}},
                    PreviousDateEnd
                )

            VAR Result =
                DIVIDE (
                    CurrentValue - PreviousWeekValue,
                    PreviousWeekValue,
                    BLANK ()
                )

            RETURN
                Result
            """),
        new(10, new("WTD", "Week", "WTD", "WTD：当前所选日期对应周累计的值。", []),
            """
            VAR CurrentDateEnd =
                LASTDATE ( {{DATE}} )

            VAR WeekdayNumber =
                WEEKDAY (
                    CurrentDateEnd,
                    3
                )

            //周一：0，周七：6
            VAR CurrentDateStart =
                DATEADD (
                    CurrentDateEnd,
                    -WeekdayNumber,
                    DAY
                )

            VAR CurrentDates =
                DATESBETWEEN (
                    {{DATE}},
                    CurrentDateStart,
                    CurrentDateEnd
                )

            VAR Result =
                CALCULATE (
                    {{MEASURE}},
                    CurrentDates
                )

            RETURN
                Result
            """),
        new(11, new("PWTD", "Week", "PWTD", "PWTD：当前所选日期对应上周累计的值。", []),
            """
            VAR CurrentDateEnd =
                LASTDATE ( {{DATE}} )

            VAR WeekdayNumber =
                WEEKDAY (
                    CurrentDateEnd,
                    3
                )

            //周一：0，周七：6
            VAR CurrentDateStart =
                DATEADD (
                    CurrentDateEnd,
                    -WeekdayNumber,
                    DAY
                )

            VAR PreviousDateStart =
                DATEADD (
                    CurrentDateStart,
                    -7,
                    DAY
                )

            VAR PreviousDateEnd =
                DATEADD (
                    CurrentDateEnd,
                    -7,
                    DAY
                )

            VAR PreviousDates =
                DATESBETWEEN (
                    {{DATE}},
                    PreviousDateStart,
                    PreviousDateEnd
                )

            VAR Result =
                IF (
                    PreviousDateStart = BLANK () || PreviousDateEnd = BLANK (),
                    BLANK (),
                    CALCULATE (
                        {{MEASURE}},
                        PreviousDates
                    )
                )

            RETURN
                Result
            """),
        new(12, new("ΔPWTD", "Week", "ΔPWTD", "ΔPWTD = WTD - PWTD。", ["WTD", "PWTD"]),
            """
            VAR CurrentDateEnd =
                LASTDATE ( {{DATE}} )

            VAR WeekdayNumber =
                WEEKDAY (
                    CurrentDateEnd,
                    3
                )

            //周一：0，周七：6
            VAR CurrentDateStart =
                DATEADD (
                    CurrentDateEnd,
                    -WeekdayNumber,
                    DAY
                )

            VAR CurrentDates =
                DATESBETWEEN (
                    {{DATE}},
                    CurrentDateStart,
                    CurrentDateEnd
                )

            VAR PreviousDateStart =
                DATEADD (
                    CurrentDateStart,
                    -7,
                    DAY
                )

            VAR PreviousDateEnd =
                DATEADD (
                    CurrentDateEnd,
                    -7,
                    DAY
                )

            VAR PreviousDates =
                DATESBETWEEN (
                    {{DATE}},
                    PreviousDateStart,
                    PreviousDateEnd
                )

            VAR WeekToDateValue =
                CALCULATE (
                    {{MEASURE}},
                    CurrentDates
                )

            VAR PreviousWeekToDateValue =
                IF (
                    PreviousDateStart = BLANK () || PreviousDateEnd = BLANK (),
                    BLANK (),
                    CALCULATE (
                        {{MEASURE}},
                        PreviousDates
                    )
                )

            VAR Result =
                WeekToDateValue - PreviousWeekToDateValue

            RETURN
                Result
            """),
        new(13, new("ΔPWTD%", "Week", "ΔPWTD%", "ΔPWTD% = ( WTD - PWTD ) / PWTD * 100%，即周累计环比。", ["WTD", "PWTD"]),
            """
            VAR CurrentDateEnd =
                LASTDATE ( {{DATE}} )

            VAR WeekdayNumber =
                WEEKDAY (
                    CurrentDateEnd,
                    3
                )

            //周一：0，周七：6
            VAR CurrentDateStart =
                DATEADD (
                    CurrentDateEnd,
                    -WeekdayNumber,
                    DAY
                )

            VAR CurrentDates =
                DATESBETWEEN (
                    {{DATE}},
                    CurrentDateStart,
                    CurrentDateEnd
                )

            VAR PreviousDateStart =
                DATEADD (
                    CurrentDateStart,
                    -7,
                    DAY
                )

            VAR PreviousDateEnd =
                DATEADD (
                    CurrentDateEnd,
                    -7,
                    DAY
                )

            VAR PreviousDates =
                DATESBETWEEN (
                    {{DATE}},
                    PreviousDateStart,
                    PreviousDateEnd
                )

            VAR WeekToDateValue =
                CALCULATE (
                    {{MEASURE}},
                    CurrentDates
                )

            VAR PreviousWeekToDateValue =
                IF (
                    PreviousDateStart = BLANK () || PreviousDateEnd = BLANK (),
                    BLANK (),
                    CALCULATE (
                        {{MEASURE}},
                        PreviousDates
                    )
                )

            VAR Result =
                DIVIDE (
                    WeekToDateValue - PreviousWeekToDateValue,
                    PreviousWeekToDateValue,
                    BLANK ()
                )

            RETURN
                Result
            """),
        new(14, new("PYWTD", "Week", "PYWTD", "PYWTD：当前所选日期对应去年相同周的累计的值。 保留参考的去年公历同周整周口径（周一开始，非ISO周），不按当前星期截断。", []),
            """
            VAR CurrentDateEnd =
                LASTDATE ( {{DATE}} )

            VAR WeekdayNumber =
                WEEKDAY (
                    CurrentDateEnd,
                    3
                )

            //周一：0，周七：6
            VAR CurrentDateStart =
                DATEADD (
                    CurrentDateEnd,
                    -WeekdayNumber,
                    DAY
                )

            VAR WeekNumber =
                WEEKNUM (
                    CurrentDateEnd,
                    2
                )

            VAR YearNumber =
                YEAR ( CurrentDateEnd )

            VAR PreviousDates =
                CALCULATETABLE (
                    {{TABLE}},
                    FILTER (
                        ALL ( {{TABLE}} ),
                        WEEKNUM (
                            {{DATE}},
                            2
                        ) = WeekNumber && YEAR ( {{DATE}} ) = YearNumber - 1
                    )
                )

            VAR PreviousDateStart =
                CALCULATE (
                    FIRSTDATE ( {{DATE}} ),
                    PreviousDates
                )

            VAR PreviousDateEnd =
                CALCULATE (
                    LASTDATE ( {{DATE}} ),
                    PreviousDates
                )

            VAR Result =
                IF (
                    PreviousDateStart = BLANK () || PreviousDateEnd = BLANK (),
                    BLANK (),
                    CALCULATE (
                        {{MEASURE}},
                        PreviousDates
                    )
                )

            RETURN
                Result
            """),
        new(15, new("ΔPYWTD", "Week", "ΔPYWTD", "ΔPYWTD = WTD - PYWTD。", ["WTD", "PYWTD"]),
            """
            VAR CurrentDateEnd =
                LASTDATE ( {{DATE}} )

            VAR WeekdayNumber =
                WEEKDAY (
                    CurrentDateEnd,
                    3
                )

            //周一：0，周七：6
            VAR CurrentDateStart =
                DATEADD (
                    CurrentDateEnd,
                    -WeekdayNumber,
                    DAY
                )

            VAR WeekNumber =
                WEEKNUM (
                    CurrentDateEnd,
                    2
                )

            VAR YearNumber =
                YEAR ( CurrentDateEnd )

            VAR PreviousDates =
                CALCULATETABLE (
                    {{TABLE}},
                    FILTER (
                        ALL ( {{TABLE}} ),
                        WEEKNUM (
                            {{DATE}},
                            2
                        ) = WeekNumber && YEAR ( {{DATE}} ) = YearNumber - 1
                    )
                )

            VAR PreviousDateStart =
                CALCULATE (
                    FIRSTDATE ( {{DATE}} ),
                    PreviousDates
                )

            VAR PreviousDateEnd =
                CALCULATE (
                    LASTDATE ( {{DATE}} ),
                    PreviousDates
                )

            VAR CurrentDates =
                DATESBETWEEN (
                    {{DATE}},
                    CurrentDateStart,
                    CurrentDateEnd
                )

            VAR WeekToDateValue =
                CALCULATE (
                    {{MEASURE}},
                    CurrentDates
                )

            VAR PreviousYearWeekToDateValue =
                IF (
                    PreviousDateStart = BLANK () || PreviousDateEnd = BLANK (),
                    BLANK (),
                    CALCULATE (
                        {{MEASURE}},
                        PreviousDates
                    )
                )

            VAR Result =
                WeekToDateValue - PreviousYearWeekToDateValue

            RETURN
                Result
            """),
        new(16, new("ΔPYWTD%", "Week", "ΔPYWTD%", "ΔPYWTD% = ( WTD - PYWTD ) / PYWTD * 100%。", ["WTD", "PYWTD"]),
            """
            VAR CurrentDateEnd =
                LASTDATE ( {{DATE}} )

            VAR WeekdayNumber =
                WEEKDAY (
                    CurrentDateEnd,
                    3
                )

            //周一：0，周七：6
            VAR CurrentDateStart =
                DATEADD (
                    CurrentDateEnd,
                    -WeekdayNumber,
                    DAY
                )

            VAR WeekNumber =
                WEEKNUM (
                    CurrentDateEnd,
                    2
                )

            VAR YearNumber =
                YEAR ( CurrentDateEnd )

            VAR PreviousDates =
                CALCULATETABLE (
                    {{TABLE}},
                    FILTER (
                        ALL ( {{TABLE}} ),
                        WEEKNUM (
                            {{DATE}},
                            2
                        ) = WeekNumber && YEAR ( {{DATE}} ) = YearNumber - 1
                    )
                )

            VAR PreviousDateStart =
                CALCULATE (
                    FIRSTDATE ( {{DATE}} ),
                    PreviousDates
                )

            VAR PreviousDateEnd =
                CALCULATE (
                    LASTDATE ( {{DATE}} ),
                    PreviousDates
                )

            VAR CurrentDates =
                DATESBETWEEN (
                    {{DATE}},
                    CurrentDateStart,
                    CurrentDateEnd
                )

            VAR WeekToDateValue =
                CALCULATE (
                    {{MEASURE}},
                    CurrentDates
                )

            VAR PreviousYearWeekToDateValue =
                IF (
                    PreviousDateStart = BLANK () || PreviousDateEnd = BLANK (),
                    BLANK (),
                    CALCULATE (
                        {{MEASURE}},
                        PreviousDates
                    )
                )

            VAR Result =
                DIVIDE (
                    WeekToDateValue - PreviousYearWeekToDateValue,
                    PreviousYearWeekToDateValue,
                    BLANK ()
                )

            RETURN
                Result
            """),
        new(17, new("PM", "Month", "PM", "PM：当前选择日期对应前一月相同日期的值，若当前选择3月30日，PM 对应的是 2 月 28 日或 2 月 29 日。", []),
            """
            VAR CurrentDateEnd =
                LASTDATE ( {{DATE}} )

            VAR PreviousDateEnd =
                DATEADD (
                    CurrentDateEnd,
                    -1,
                    MONTH
                )

            VAR Result =
                CALCULATE (
                    {{MEASURE}},
                    PreviousDateEnd
                )

            RETURN
                Result
            """),
        new(18, new("ΔPM", "Month", "ΔPM", "ΔPM = AC - PM。", ["AC", "PM"]),
            """
            VAR CurrentDateEnd =
                LASTDATE ( {{DATE}} )

            VAR PreviousDateEnd =
                DATEADD (
                    CurrentDateEnd,
                    -1,
                    MONTH
                )

            VAR CurrentValue =
                CALCULATE (
                    {{MEASURE}},
                    CurrentDateEnd
                )

            VAR PreviousMonthValue =
                CALCULATE (
                    {{MEASURE}},
                    PreviousDateEnd
                )

            VAR Result =
                CurrentValue - PreviousMonthValue

            RETURN
                Result
            """),
        new(19, new("MOM%", "Month", "MOM%", "MOM% = ( AC - PM ) / PM * 100%，即环比上月。", ["AC", "PM"]),
            """
            VAR CurrentDateEnd =
                LASTDATE ( {{DATE}} )

            VAR PreviousDateEnd =
                DATEADD (
                    CurrentDateEnd,
                    -1,
                    MONTH
                )

            VAR CurrentValue =
                CALCULATE (
                    {{MEASURE}},
                    CurrentDateEnd
                )

            VAR PreviousMonthValue =
                CALCULATE (
                    {{MEASURE}},
                    PreviousDateEnd
                )

            VAR Result =
                DIVIDE (
                    CurrentValue - PreviousMonthValue,
                    PreviousMonthValue,
                    BLANK ()
                )

            RETURN
                Result
            """),
        new(20, new("MTD", "Month", "MTD", "MTD：当前所选日期对应月累计的值。", []),
            """
            VAR CurrentDateEnd =
                LASTDATE ( {{DATE}} )

            VAR CurrentDateStart =
                STARTOFMONTH ( CurrentDateEnd )

            VAR CurrentDates =
                DATESBETWEEN (
                    {{DATE}},
                    CurrentDateStart,
                    CurrentDateEnd
                )

            VAR Result =
                CALCULATE (
                    {{MEASURE}},
                    CurrentDates
                )

            RETURN
                Result
            """),
        new(21, new("PMTD", "Month", "PMTD", "PMTD：当前所选日期对应上月累计的值。", []),
            """
            VAR CurrentDateEnd =
                LASTDATE ( {{DATE}} )

            VAR CurrentDateStart =
                STARTOFMONTH ( CurrentDateEnd )

            VAR PreviousDateStart =
                DATEADD (
                    CurrentDateStart,
                    -1,
                    MONTH
                )

            VAR PreviousDateEnd =
                IF (
                    CurrentDateEnd = ENDOFMONTH ( CurrentDateEnd ),
                    ENDOFMONTH ( PreviousDateStart ),
                    DATEADD (
                        CurrentDateEnd,
                        -1,
                        MONTH
                    )
                )

            VAR PreviousDates =
                DATESBETWEEN (
                    {{DATE}},
                    PreviousDateStart,
                    PreviousDateEnd
                )

            VAR Result =
                IF (
                    PreviousDateStart = BLANK () || PreviousDateEnd = BLANK (),
                    BLANK (),
                    CALCULATE (
                        {{MEASURE}},
                        PreviousDates
                    )
                )

            RETURN
                Result
            """),
        new(22, new("ΔPMTD", "Month", "ΔPMTD", "ΔPMTD = MTD - PMTD。", ["MTD", "PMTD"]),
            """
            VAR CurrentDateEnd =
                LASTDATE ( {{DATE}} )

            VAR CurrentDateStart =
                STARTOFMONTH ( CurrentDateEnd )

            VAR PreviousDateStart =
                DATEADD (
                    CurrentDateStart,
                    -1,
                    MONTH
                )

            VAR PreviousDateEnd =
                IF (
                    CurrentDateEnd = ENDOFMONTH ( CurrentDateEnd ),
                    ENDOFMONTH ( PreviousDateStart ),
                    DATEADD (
                        CurrentDateEnd,
                        -1,
                        MONTH
                    )
                )

            VAR CurrentDates =
                DATESBETWEEN (
                    {{DATE}},
                    CurrentDateStart,
                    CurrentDateEnd
                )

            VAR PreviousDates =
                DATESBETWEEN (
                    {{DATE}},
                    PreviousDateStart,
                    PreviousDateEnd
                )

            VAR MonthToDateValue =
                CALCULATE (
                    {{MEASURE}},
                    CurrentDates
                )

            VAR PreviousMonthToDateValue =
                IF (
                    PreviousDateStart = BLANK () || PreviousDateEnd = BLANK (),
                    BLANK (),
                    CALCULATE (
                        {{MEASURE}},
                        PreviousDates
                    )
                )

            VAR Result =
                MonthToDateValue - PreviousMonthToDateValue

            RETURN
                Result
            """),
        new(23, new("ΔPMTD%", "Month", "ΔPMTD%", "ΔPMTD% = ( MTD - PMTD ) / PMTD * 100%。", ["MTD", "PMTD"]),
            """
            VAR CurrentDateEnd =
                LASTDATE ( {{DATE}} )

            VAR CurrentDateStart =
                STARTOFMONTH ( CurrentDateEnd )

            VAR PreviousDateStart =
                DATEADD (
                    CurrentDateStart,
                    -1,
                    MONTH
                )

            VAR PreviousDateEnd =
                IF (
                    CurrentDateEnd = ENDOFMONTH ( CurrentDateEnd ),
                    ENDOFMONTH ( PreviousDateStart ),
                    DATEADD (
                        CurrentDateEnd,
                        -1,
                        MONTH
                    )
                )

            VAR CurrentDates =
                DATESBETWEEN (
                    {{DATE}},
                    CurrentDateStart,
                    CurrentDateEnd
                )

            VAR PreviousDates =
                DATESBETWEEN (
                    {{DATE}},
                    PreviousDateStart,
                    PreviousDateEnd
                )

            VAR MonthToDateValue =
                CALCULATE (
                    {{MEASURE}},
                    CurrentDates
                )

            VAR PreviousMonthToDateValue =
                IF (
                    PreviousDateStart = BLANK () || PreviousDateEnd = BLANK (),
                    BLANK (),
                    CALCULATE (
                        {{MEASURE}},
                        PreviousDates
                    )
                )

            VAR Result =
                DIVIDE (
                    MonthToDateValue - PreviousMonthToDateValue,
                    PreviousMonthToDateValue,
                    BLANK ()
                )

            RETURN
                Result
            """),
        new(24, new("PYMTD", "Month", "PYMTD", "PYMTD：当前所选日期对应去年相同月的累计的值。", []),
            """
            VAR CurrentDateEnd =
                LASTDATE ( {{DATE}} )

            VAR CurrentDateStart =
                STARTOFMONTH ( CurrentDateEnd )

            VAR PreviousDateStart =
                DATEADD (
                    CurrentDateStart,
                    -1,
                    YEAR
                )

            VAR PreviousDateEnd =
                IF (
                    CurrentDateEnd = ENDOFMONTH ( CurrentDateEnd ),
                    ENDOFMONTH ( PreviousDateStart ),
                    DATEADD (
                        CurrentDateEnd,
                        -1,
                        YEAR
                    )
                )

            VAR PreviousDates =
                DATESBETWEEN (
                    {{DATE}},
                    PreviousDateStart,
                    PreviousDateEnd
                )

            VAR Result =
                IF (
                    PreviousDateStart = BLANK () || PreviousDateEnd = BLANK (),
                    BLANK (),
                    CALCULATE (
                        {{MEASURE}},
                        PreviousDates
                    )
                )

            RETURN
                Result
            """),
        new(25, new("ΔPYMTD", "Month", "ΔPYMTD", "ΔPYMTD = MTD - PYMTD。", ["MTD", "PYMTD"]),
            """
            VAR CurrentDateEnd =
                LASTDATE ( {{DATE}} )

            VAR CurrentDateStart =
                STARTOFMONTH ( CurrentDateEnd )

            VAR PreviousDateStart =
                DATEADD (
                    CurrentDateStart,
                    -1,
                    YEAR
                )

            VAR PreviousDateEnd =
                IF (
                    CurrentDateEnd = ENDOFMONTH ( CurrentDateEnd ),
                    ENDOFMONTH ( PreviousDateStart ),
                    DATEADD (
                        CurrentDateEnd,
                        -1,
                        YEAR
                    )
                )

            VAR CurrentDates =
                DATESBETWEEN (
                    {{DATE}},
                    CurrentDateStart,
                    CurrentDateEnd
                )

            VAR PreviousDates =
                DATESBETWEEN (
                    {{DATE}},
                    PreviousDateStart,
                    PreviousDateEnd
                )

            VAR MonthToDateValue =
                CALCULATE (
                    {{MEASURE}},
                    CurrentDates
                )

            VAR PreviousYearMonthToDateValue =
                IF (
                    PreviousDateStart = BLANK () || PreviousDateEnd = BLANK (),
                    BLANK (),
                    CALCULATE (
                        {{MEASURE}},
                        PreviousDates
                    )
                )

            VAR Result =
                MonthToDateValue - PreviousYearMonthToDateValue

            RETURN
                Result
            """),
        new(26, new("ΔPYMTD%", "Month", "ΔPYMTD%", "ΔPYMTD% = ( MTD - PYMTD ) / PYMTD * 100%。", ["MTD", "PYMTD"]),
            """
            VAR CurrentDateEnd =
                LASTDATE ( {{DATE}} )

            VAR CurrentDateStart =
                STARTOFMONTH ( CurrentDateEnd )

            VAR PreviousDateStart =
                DATEADD (
                    CurrentDateStart,
                    -1,
                    YEAR
                )

            VAR PreviousDateEnd =
                IF (
                    CurrentDateEnd = ENDOFMONTH ( CurrentDateEnd ),
                    ENDOFMONTH ( PreviousDateStart ),
                    DATEADD (
                        CurrentDateEnd,
                        -1,
                        YEAR
                    )
                )

            VAR CurrentDates =
                DATESBETWEEN (
                    {{DATE}},
                    CurrentDateStart,
                    CurrentDateEnd
                )

            VAR PreviousDates =
                DATESBETWEEN (
                    {{DATE}},
                    PreviousDateStart,
                    PreviousDateEnd
                )

            VAR MonthToDateValue =
                CALCULATE (
                    {{MEASURE}},
                    CurrentDates
                )

            VAR PreviousYearMonthToDateValue =
                IF (
                    PreviousDateStart = BLANK () || PreviousDateEnd = BLANK (),
                    BLANK (),
                    CALCULATE (
                        {{MEASURE}},
                        PreviousDates
                    )
                )

            VAR Result =
                DIVIDE (
                    MonthToDateValue - PreviousYearMonthToDateValue,
                    PreviousYearMonthToDateValue,
                    BLANK ()
                )

            RETURN
                Result
            """),
        new(27, new("PQ", "Quarter", "PQ", "PQ：当前选择日期对应前一季度相同天数的值，若当前选择 5 月 30 日，PQ 对应的是 2 月 28 日或 2 月 29 日。", []),
            """
            VAR CurrentDateEnd =
                LASTDATE ( {{DATE}} )

            VAR CurrentDateStart =
                STARTOFQUARTER ( CurrentDateEnd )

            VAR PreviousDateStart =
                DATEADD (
                    CurrentDateStart,
                    -1,
                    QUARTER
                )

            VAR PreviousDateEnd =
                DATEADD (
                    CurrentDateEnd,
                    -1,
                    QUARTER
                )

            VAR Result =
                CALCULATE (
                    {{MEASURE}},
                    PreviousDateEnd
                )

            RETURN
                Result
            """),
        new(28, new("ΔPQ", "Quarter", "ΔPQ", "ΔPQ = AC - PQ。", ["AC", "PQ"]),
            """
            VAR CurrentDateEnd =
                LASTDATE ( {{DATE}} )

            VAR PreviousDateEnd =
                DATEADD (
                    CurrentDateEnd,
                    -1,
                    QUARTER
                )

            VAR CurrentValue =
                CALCULATE (
                    {{MEASURE}},
                    CurrentDateEnd
                )

            VAR PreviousQuarterValue =
                CALCULATE (
                    {{MEASURE}},
                    PreviousDateEnd
                )

            VAR Result =
                CurrentValue - PreviousQuarterValue

            RETURN
                Result
            """),
        new(29, new("QOQ%", "Quarter", "QOQ%", "QOQ% = ( AC - PQ ) / PQ * 100%，即环比上季度。", ["AC", "PQ"]),
            """
            VAR CurrentDateEnd =
                LASTDATE ( {{DATE}} )

            VAR PreviousDateEnd =
                DATEADD (
                    CurrentDateEnd,
                    -1,
                    QUARTER
                )

            VAR CurrentValue =
                CALCULATE (
                    {{MEASURE}},
                    CurrentDateEnd
                )

            VAR PreviousQuarterValue =
                CALCULATE (
                    {{MEASURE}},
                    PreviousDateEnd
                )

            VAR Result =
                DIVIDE (
                    CurrentValue - PreviousQuarterValue,
                    PreviousQuarterValue,
                    BLANK ()
                )

            RETURN
                Result
            """),
        new(30, new("QTD", "Quarter", "QTD", "QTD：当前所选日期对应季度累计的值。", []),
            """
            VAR CurrentDateEnd =
                LASTDATE ( {{DATE}} )

            VAR CurrentDateStart =
                STARTOFQUARTER ( CurrentDateEnd )

            VAR CurrentDates =
                DATESBETWEEN (
                    {{DATE}},
                    CurrentDateStart,
                    CurrentDateEnd
                )

            VAR Result =
                CALCULATE (
                    {{MEASURE}},
                    CurrentDates
                )

            RETURN
                Result
            """),
        new(31, new("PQTD", "Quarter", "PQTD", "PQTD：当前所选日期对应上季度累计的值。", []),
            """
            VAR CurrentDateEnd =
                LASTDATE ( {{DATE}} )

            VAR CurrentDateStart =
                STARTOFQUARTER ( CurrentDateEnd )

            VAR PreviousDateStart =
                DATEADD (
                    CurrentDateStart,
                    -1,
                    QUARTER
                )

            VAR PreviousDateEnd =
                SWITCH (
                    TRUE (),
                    CurrentDateEnd = ENDOFQUARTER ( CurrentDateEnd ),
                    ENDOFQUARTER ( PreviousDateStart ),
                    CurrentDateEnd = ENDOFMONTH ( CurrentDateEnd ) && CurrentDateEnd < ENDOFQUARTER ( CurrentDateEnd ),
                    ENDOFMONTH (
                        DATEADD (
                            CurrentDateEnd,
                            -1,
                            QUARTER
                        )
                    ),
                    DATEADD (
                        CurrentDateEnd,
                        -1,
                        QUARTER
                    )
                )

            VAR PreviousDates =
                DATESBETWEEN (
                    {{DATE}},
                    PreviousDateStart,
                    PreviousDateEnd
                )

            VAR Result =
                IF (
                    PreviousDateStart = BLANK () || PreviousDateEnd = BLANK (),
                    BLANK (),
                    CALCULATE (
                        {{MEASURE}},
                        PreviousDates
                    )
                )

            RETURN
                Result
            """),
        new(32, new("ΔPQTD", "Quarter", "ΔPQTD", "ΔPQTD = QTD - PQTD。", ["QTD", "PQTD"]),
            """
            VAR CurrentDateEnd =
                LASTDATE ( {{DATE}} )

            VAR CurrentDateStart =
                STARTOFQUARTER ( CurrentDateEnd )

            VAR PreviousDateStart =
                DATEADD (
                    CurrentDateStart,
                    -1,
                    QUARTER
                )

            VAR PreviousDateEnd =
                SWITCH (
                    TRUE (),
                    CurrentDateEnd = ENDOFQUARTER ( CurrentDateEnd ),
                    ENDOFQUARTER ( PreviousDateStart ),
                    CurrentDateEnd = ENDOFMONTH ( CurrentDateEnd ) && CurrentDateEnd < ENDOFQUARTER ( CurrentDateEnd ),
                    ENDOFMONTH (
                        DATEADD (
                            CurrentDateEnd,
                            -1,
                            QUARTER
                        )
                    ),
                    DATEADD (
                        CurrentDateEnd,
                        -1,
                        QUARTER
                    )
                )

            VAR CurrentDates =
                DATESBETWEEN (
                    {{DATE}},
                    CurrentDateStart,
                    CurrentDateEnd
                )

            VAR PreviousDates =
                DATESBETWEEN (
                    {{DATE}},
                    PreviousDateStart,
                    PreviousDateEnd
                )

            VAR QuarterToDateValue =
                CALCULATE (
                    {{MEASURE}},
                    CurrentDates
                )

            VAR PreviousQuarterToDateValue =
                IF (
                    PreviousDateStart = BLANK () || PreviousDateEnd = BLANK (),
                    BLANK (),
                    CALCULATE (
                        {{MEASURE}},
                        PreviousDates
                    )
                )

            VAR Result =
                QuarterToDateValue - PreviousQuarterToDateValue

            RETURN
                Result
            """),
        new(33, new("ΔPQTD%", "Quarter", "ΔPQTD%", "ΔPQTD% = ( QTD - PQTD ) / PQTD * 100%。", ["QTD", "PQTD"]),
            """
            VAR CurrentDateEnd =
                LASTDATE ( {{DATE}} )

            VAR CurrentDateStart =
                STARTOFQUARTER ( CurrentDateEnd )

            VAR PreviousDateStart =
                DATEADD (
                    CurrentDateStart,
                    -1,
                    QUARTER
                )

            VAR PreviousDateEnd =
                SWITCH (
                    TRUE (),
                    CurrentDateEnd = ENDOFQUARTER ( CurrentDateEnd ),
                    ENDOFQUARTER ( PreviousDateStart ),
                    CurrentDateEnd = ENDOFMONTH ( CurrentDateEnd ) && CurrentDateEnd < ENDOFQUARTER ( CurrentDateEnd ),
                    ENDOFMONTH (
                        DATEADD (
                            CurrentDateEnd,
                            -1,
                            QUARTER
                        )
                    ),
                    DATEADD (
                        CurrentDateEnd,
                        -1,
                        QUARTER
                    )
                )

            VAR CurrentDates =
                DATESBETWEEN (
                    {{DATE}},
                    CurrentDateStart,
                    CurrentDateEnd
                )

            VAR PreviousDates =
                DATESBETWEEN (
                    {{DATE}},
                    PreviousDateStart,
                    PreviousDateEnd
                )

            VAR QuarterToDateValue =
                CALCULATE (
                    {{MEASURE}},
                    CurrentDates
                )

            VAR PreviousQuarterToDateValue =
                IF (
                    PreviousDateStart = BLANK () || PreviousDateEnd = BLANK (),
                    BLANK (),
                    CALCULATE (
                        {{MEASURE}},
                        PreviousDates
                    )
                )

            VAR Result =
                DIVIDE (
                    QuarterToDateValue - PreviousQuarterToDateValue,
                    PreviousQuarterToDateValue,
                    BLANK ()
                )

            RETURN
                Result
            """),
        new(34, new("PYQTD", "Quarter", "PYQTD", "PYQTD：当前所选日期对应去年相同季度的累计的值。", []),
            """
            VAR CurrentDateEnd =
                LASTDATE ( {{DATE}} )

            VAR CurrentDateStart =
                STARTOFQUARTER ( CurrentDateEnd )

            VAR PreviousDateStart =
                DATEADD (
                    CurrentDateStart,
                    -1,
                    YEAR
                )

            VAR PreviousDateEnd =
                SWITCH (
                    TRUE (),
                    CurrentDateEnd = ENDOFQUARTER ( CurrentDateEnd ),
                    ENDOFQUARTER ( PreviousDateStart ),
                    CurrentDateEnd = ENDOFMONTH ( CurrentDateEnd ) && CurrentDateEnd < ENDOFQUARTER ( CurrentDateEnd ),
                    ENDOFMONTH (
                        DATEADD (
                            CurrentDateEnd,
                            -1,
                            YEAR
                        )
                    ),
                    DATEADD (
                        CurrentDateEnd,
                        -1,
                        YEAR
                    )
                )

            VAR PreviousDates =
                DATESBETWEEN (
                    {{DATE}},
                    PreviousDateStart,
                    PreviousDateEnd
                )

            VAR Result =
                IF (
                    PreviousDateStart = BLANK () || PreviousDateEnd = BLANK (),
                    BLANK (),
                    CALCULATE (
                        {{MEASURE}},
                        PreviousDates
                    )
                )

            RETURN
                Result
            """),
        new(35, new("ΔPYQTD", "Quarter", "ΔPYQTD", "ΔPYQTD = QTD - PYQTD。", ["QTD", "PYQTD"]),
            """
            VAR CurrentDateEnd =
                LASTDATE ( {{DATE}} )

            VAR CurrentDateStart =
                STARTOFQUARTER ( CurrentDateEnd )

            VAR PreviousDateStart =
                DATEADD (
                    CurrentDateStart,
                    -1,
                    YEAR
                )

            VAR PreviousDateEnd =
                SWITCH (
                    TRUE (),
                    CurrentDateEnd = ENDOFQUARTER ( CurrentDateEnd ),
                    ENDOFQUARTER ( PreviousDateStart ),
                    CurrentDateEnd = ENDOFMONTH ( CurrentDateEnd ) && CurrentDateEnd < ENDOFQUARTER ( CurrentDateEnd ),
                    ENDOFMONTH (
                        DATEADD (
                            CurrentDateEnd,
                            -1,
                            YEAR
                        )
                    ),
                    DATEADD (
                        CurrentDateEnd,
                        -1,
                        YEAR
                    )
                )

            VAR CurrentDates =
                DATESBETWEEN (
                    {{DATE}},
                    CurrentDateStart,
                    CurrentDateEnd
                )

            VAR PreviousDates =
                DATESBETWEEN (
                    {{DATE}},
                    PreviousDateStart,
                    PreviousDateEnd
                )

            VAR QuarterToDateValue =
                CALCULATE (
                    {{MEASURE}},
                    CurrentDates
                )

            VAR PreviousYearQuarterToDateValue =
                IF (
                    PreviousDateStart = BLANK () || PreviousDateEnd = BLANK (),
                    BLANK (),
                    CALCULATE (
                        {{MEASURE}},
                        PreviousDates
                    )
                )

            VAR Result =
                QuarterToDateValue - PreviousYearQuarterToDateValue

            RETURN
                Result
            """),
        new(36, new("ΔPYQTD%", "Quarter", "ΔPYQTD%", "ΔPYQTD% = ( QTD - PYQTD ) / PYQTD * 100%。", ["QTD", "PYQTD"]),
            """
            VAR CurrentDateEnd =
                LASTDATE ( {{DATE}} )

            VAR CurrentDateStart =
                STARTOFQUARTER ( CurrentDateEnd )

            VAR PreviousDateStart =
                DATEADD (
                    CurrentDateStart,
                    -1,
                    YEAR
                )

            VAR PreviousDateEnd =
                SWITCH (
                    TRUE (),
                    CurrentDateEnd = ENDOFQUARTER ( CurrentDateEnd ),
                    ENDOFQUARTER ( PreviousDateStart ),
                    CurrentDateEnd = ENDOFMONTH ( CurrentDateEnd ) && CurrentDateEnd < ENDOFQUARTER ( CurrentDateEnd ),
                    ENDOFMONTH (
                        DATEADD (
                            CurrentDateEnd,
                            -1,
                            YEAR
                        )
                    ),
                    DATEADD (
                        CurrentDateEnd,
                        -1,
                        YEAR
                    )
                )

            VAR CurrentDates =
                DATESBETWEEN (
                    {{DATE}},
                    CurrentDateStart,
                    CurrentDateEnd
                )

            VAR PreviousDates =
                DATESBETWEEN (
                    {{DATE}},
                    PreviousDateStart,
                    PreviousDateEnd
                )

            VAR QuarterToDateValue =
                CALCULATE (
                    {{MEASURE}},
                    CurrentDates
                )

            VAR PreviousYearQuarterToDateValue =
                IF (
                    PreviousDateStart = BLANK () || PreviousDateEnd = BLANK (),
                    BLANK (),
                    CALCULATE (
                        {{MEASURE}},
                        PreviousDates
                    )
                )

            VAR Result =
                DIVIDE (
                    QuarterToDateValue - PreviousYearQuarterToDateValue,
                    PreviousYearQuarterToDateValue,
                    BLANK ()
                )

            RETURN
                Result
            """),
        new(37, new("PH", "HalfYear", "PH", "PH：当前选择日期对应前一半年度相同天数的值，若当前选择 11 月 30 日，PH 对应的是 5 月 30 日。", []),
            """
            VAR CurrentDateEnd =
                LASTDATE ( {{DATE}} )

            VAR PreviousDateEnd =
                DATEADD (
                    CurrentDateEnd,
                    -6,
                    MONTH
                )

            VAR Result =
                CALCULATE (
                    {{MEASURE}},
                    PreviousDateEnd
                )

            RETURN
                Result
            """),
        new(38, new("ΔPH", "HalfYear", "ΔPH", "ΔPH = AC - PH。", ["AC", "PH"]),
            """
            VAR CurrentDateEnd =
                LASTDATE ( {{DATE}} )

            VAR PreviousDateEnd =
                DATEADD (
                    CurrentDateEnd,
                    -6,
                    MONTH
                )

            VAR CurrentValue =
                CALCULATE (
                    {{MEASURE}},
                    CurrentDateEnd
                )

            VAR PreviousHalfYearValue =
                CALCULATE (
                    {{MEASURE}},
                    PreviousDateEnd
                )

            VAR Result =
                CurrentValue - PreviousHalfYearValue

            RETURN
                Result
            """),
        new(39, new("HOH%", "HalfYear", "HOH%", "HOH% = ( AC - PH ) / PH * 100%，即环比前一个半年度。", ["AC", "PH"]),
            """
            VAR CurrentDateEnd =
                LASTDATE ( {{DATE}} )

            VAR PreviousDateEnd =
                DATEADD (
                    CurrentDateEnd,
                    -6,
                    MONTH
                )

            VAR CurrentValue =
                CALCULATE (
                    {{MEASURE}},
                    CurrentDateEnd
                )

            VAR PreviousHalfYearValue =
                CALCULATE (
                    {{MEASURE}},
                    PreviousDateEnd
                )

            VAR Result =
                DIVIDE (
                    CurrentValue - PreviousHalfYearValue,
                    PreviousHalfYearValue,
                    BLANK ()
                )

            RETURN
                Result
            """),
        new(40, new("HTD", "HalfYear", "HTD", "HTD：当前所选日期对应半年度累计的值。", []),
            """
            VAR CurrentDateEnd =
                LASTDATE ( {{DATE}} )

            VAR YearStart =
                STARTOFYEAR ( CurrentDateEnd )

            VAR CurrentDateStart =
                IF (
                    MONTH ( CurrentDateEnd ) < 7,
                    YearStart,
                    DATEADD (
                        YearStart,
                        6,
                        MONTH
                    )
                )

            VAR CurrentDates =
                DATESBETWEEN (
                    {{DATE}},
                    CurrentDateStart,
                    CurrentDateEnd
                )

            VAR Result =
                CALCULATE (
                    {{MEASURE}},
                    CurrentDates
                )

            RETURN
                Result
            """),
        new(41, new("PHTD", "HalfYear", "PHTD", "PHTD：当前所选日期对应前一个半年度累计的值。", []),
            """
            VAR CurrentDateEnd =
                LASTDATE ( {{DATE}} )

            VAR YearStart =
                STARTOFYEAR ( CurrentDateEnd )

            VAR CurrentDateStart =
                IF (
                    MONTH ( CurrentDateEnd ) < 7,
                    YearStart,
                    DATEADD (
                        YearStart,
                        6,
                        MONTH
                    )
                )

            VAR PreviousDateStart =
                IF (
                    MONTH ( CurrentDateEnd ) < 7,
                    DATEADD (
                        YearStart,
                        -6,
                        MONTH
                    ),
                    YearStart
                )

            VAR PreviousDateEnd =
                SWITCH (
                    TRUE (),
                    MOD (
                        MONTH ( CurrentDateEnd ),
                        6
                    ) = 0 && CurrentDateEnd = ENDOFMONTH ( CurrentDateEnd ),
                    ENDOFMONTH (
                        DATEADD (
                            CurrentDateEnd,
                            -6,
                            MONTH
                        )
                    ),
                    DATEADD (
                        CurrentDateEnd,
                        -6,
                        MONTH
                    )
                )

            VAR PreviousDates =
                DATESBETWEEN (
                    {{DATE}},
                    PreviousDateStart,
                    PreviousDateEnd
                )

            VAR Result =
                IF (
                    PreviousDateStart = BLANK () || PreviousDateEnd = BLANK (),
                    BLANK (),
                    CALCULATE (
                        {{MEASURE}},
                        PreviousDates
                    )
                )

            RETURN
                Result
            """),
        new(42, new("ΔPHTD", "HalfYear", "ΔPHTD", "ΔPHTD = HTD - PHTD。", ["HTD", "PHTD"]),
            """
            VAR CurrentDateEnd =
                LASTDATE ( {{DATE}} )

            VAR YearStart =
                STARTOFYEAR ( CurrentDateEnd )

            VAR CurrentDateStart =
                IF (
                    MONTH ( CurrentDateEnd ) < 7,
                    YearStart,
                    DATEADD (
                        YearStart,
                        6,
                        MONTH
                    )
                )

            VAR PreviousDateStart =
                IF (
                    MONTH ( CurrentDateEnd ) < 7,
                    DATEADD (
                        YearStart,
                        -6,
                        MONTH
                    ),
                    YearStart
                )

            VAR PreviousDateEnd =
                SWITCH (
                    TRUE (),
                    MOD (
                        MONTH ( CurrentDateEnd ),
                        6
                    ) = 0 && CurrentDateEnd = ENDOFMONTH ( CurrentDateEnd ),
                    ENDOFMONTH (
                        DATEADD (
                            CurrentDateEnd,
                            -6,
                            MONTH
                        )
                    ),
                    DATEADD (
                        CurrentDateEnd,
                        -6,
                        MONTH
                    )
                )

            VAR CurrentDates =
                DATESBETWEEN (
                    {{DATE}},
                    CurrentDateStart,
                    CurrentDateEnd
                )

            VAR PreviousDates =
                DATESBETWEEN (
                    {{DATE}},
                    PreviousDateStart,
                    PreviousDateEnd
                )

            VAR HalfYearToDateValue =
                CALCULATE (
                    {{MEASURE}},
                    CurrentDates
                )

            VAR PreviousHalfYearToDateValue =
                IF (
                    PreviousDateStart = BLANK () || PreviousDateEnd = BLANK (),
                    BLANK (),
                    CALCULATE (
                        {{MEASURE}},
                        PreviousDates
                    )
                )

            VAR Result =
                HalfYearToDateValue - PreviousHalfYearToDateValue

            RETURN
                Result
            """),
        new(43, new("ΔPHTD%", "HalfYear", "ΔPHTD%", "ΔPHTD% = ( HTD - PHTD ) / PHTD * 100%。", ["HTD", "PHTD"]),
            """
            VAR CurrentDateEnd =
                LASTDATE ( {{DATE}} )

            VAR YearStart =
                STARTOFYEAR ( CurrentDateEnd )

            VAR CurrentDateStart =
                IF (
                    MONTH ( CurrentDateEnd ) < 7,
                    YearStart,
                    DATEADD (
                        YearStart,
                        6,
                        MONTH
                    )
                )

            VAR PreviousDateStart =
                IF (
                    MONTH ( CurrentDateEnd ) < 7,
                    DATEADD (
                        YearStart,
                        -6,
                        MONTH
                    ),
                    YearStart
                )

            VAR PreviousDateEnd =
                SWITCH (
                    TRUE (),
                    MOD (
                        MONTH ( CurrentDateEnd ),
                        6
                    ) = 0 && CurrentDateEnd = ENDOFMONTH ( CurrentDateEnd ),
                    ENDOFMONTH (
                        DATEADD (
                            CurrentDateEnd,
                            -6,
                            MONTH
                        )
                    ),
                    DATEADD (
                        CurrentDateEnd,
                        -6,
                        MONTH
                    )
                )

            VAR CurrentDates =
                DATESBETWEEN (
                    {{DATE}},
                    CurrentDateStart,
                    CurrentDateEnd
                )

            VAR PreviousDates =
                DATESBETWEEN (
                    {{DATE}},
                    PreviousDateStart,
                    PreviousDateEnd
                )

            VAR HalfYearToDateValue =
                CALCULATE (
                    {{MEASURE}},
                    CurrentDates
                )

            VAR PreviousHalfYearToDateValue =
                IF (
                    PreviousDateStart = BLANK () || PreviousDateEnd = BLANK (),
                    BLANK (),
                    CALCULATE (
                        {{MEASURE}},
                        PreviousDates
                    )
                )

            VAR Result =
                DIVIDE (
                    HalfYearToDateValue - PreviousHalfYearToDateValue,
                    PreviousHalfYearToDateValue,
                    BLANK ()
                )

            RETURN
                Result
            """),
        new(44, new("PYHTD", "HalfYear", "PYHTD", "PYHTD：当前所选日期对应去年相同半年度的累计的值。", []),
            """
            VAR CurrentDateEnd =
                LASTDATE ( {{DATE}} )

            VAR YearStart =
                STARTOFYEAR ( CurrentDateEnd )

            VAR CurrentDateStart =
                IF (
                    MONTH ( CurrentDateEnd ) < 7,
                    YearStart,
                    DATEADD (
                        YearStart,
                        6,
                        MONTH
                    )
                )

            VAR PreviousDateStart =
                IF (
                    MONTH ( CurrentDateEnd ) < 7,
                    DATEADD (
                        YearStart,
                        -1,
                        YEAR
                    ),
                    DATEADD (
                        YearStart,
                        -6,
                        MONTH
                    )
                )

            VAR PreviousDateEnd =
                DATEADD (
                    CurrentDateEnd,
                    -1,
                    YEAR
                )

            VAR PreviousDates =
                DATESBETWEEN (
                    {{DATE}},
                    PreviousDateStart,
                    PreviousDateEnd
                )

            VAR Result =
                IF (
                    PreviousDateStart = BLANK () || PreviousDateEnd = BLANK (),
                    BLANK (),
                    CALCULATE (
                        {{MEASURE}},
                        PreviousDates
                    )
                )

            RETURN
                Result
            """),
        new(45, new("ΔPYHTD", "HalfYear", "ΔPYHTD", "ΔPYHTD = HTD - PYHTD。", ["HTD", "PYHTD"]),
            """
            VAR CurrentDateEnd =
                LASTDATE ( {{DATE}} )

            VAR YearStart =
                STARTOFYEAR ( CurrentDateEnd )

            VAR CurrentDateStart =
                IF (
                    MONTH ( CurrentDateEnd ) < 7,
                    YearStart,
                    DATEADD (
                        YearStart,
                        6,
                        MONTH
                    )
                )

            VAR PreviousDateStart =
                IF (
                    MONTH ( CurrentDateEnd ) < 7,
                    DATEADD (
                        YearStart,
                        -1,
                        YEAR
                    ),
                    DATEADD (
                        YearStart,
                        -6,
                        MONTH
                    )
                )

            VAR PreviousDateEnd =
                DATEADD (
                    CurrentDateEnd,
                    -1,
                    YEAR
                )

            VAR CurrentDates =
                DATESBETWEEN (
                    {{DATE}},
                    CurrentDateStart,
                    CurrentDateEnd
                )

            VAR PreviousDates =
                DATESBETWEEN (
                    {{DATE}},
                    PreviousDateStart,
                    PreviousDateEnd
                )

            VAR HalfYearToDateValue =
                CALCULATE (
                    {{MEASURE}},
                    CurrentDates
                )

            VAR PreviousYearHalfYearToDateValue =
                IF (
                    PreviousDateStart = BLANK () || PreviousDateEnd = BLANK (),
                    BLANK (),
                    CALCULATE (
                        {{MEASURE}},
                        PreviousDates
                    )
                )

            VAR Result =
                HalfYearToDateValue - PreviousYearHalfYearToDateValue

            RETURN
                Result
            """),
        new(46, new("ΔPYHTD%", "HalfYear", "ΔPYHTD%", "ΔPYHTD% = ( HTD - PYHTD ) / PYHTD * 100%。", ["HTD", "PYHTD"]),
            """
            VAR CurrentDateEnd =
                LASTDATE ( {{DATE}} )

            VAR YearStart =
                STARTOFYEAR ( CurrentDateEnd )

            VAR CurrentDateStart =
                IF (
                    MONTH ( CurrentDateEnd ) < 7,
                    YearStart,
                    DATEADD (
                        YearStart,
                        6,
                        MONTH
                    )
                )

            VAR PreviousDateStart =
                IF (
                    MONTH ( CurrentDateEnd ) < 7,
                    DATEADD (
                        YearStart,
                        -1,
                        YEAR
                    ),
                    DATEADD (
                        YearStart,
                        -6,
                        MONTH
                    )
                )

            VAR PreviousDateEnd =
                DATEADD (
                    CurrentDateEnd,
                    -1,
                    YEAR
                )

            VAR CurrentDates =
                DATESBETWEEN (
                    {{DATE}},
                    CurrentDateStart,
                    CurrentDateEnd
                )

            VAR PreviousDates =
                DATESBETWEEN (
                    {{DATE}},
                    PreviousDateStart,
                    PreviousDateEnd
                )

            VAR HalfYearToDateValue =
                CALCULATE (
                    {{MEASURE}},
                    CurrentDates
                )

            VAR PreviousYearHalfYearToDateValue =
                IF (
                    PreviousDateStart = BLANK () || PreviousDateEnd = BLANK (),
                    BLANK (),
                    CALCULATE (
                        {{MEASURE}},
                        PreviousDates
                    )
                )

            VAR Result =
                DIVIDE (
                    HalfYearToDateValue - PreviousYearHalfYearToDateValue,
                    PreviousYearHalfYearToDateValue,
                    BLANK ()
                )

            RETURN
                Result
            """),
        new(47, new("YTD", "Year", "YTD", "YTD：当前所选日期对应年累计的值。", []),
            """
            VAR CurrentDateEnd =
                LASTDATE ( {{DATE}} )

            VAR CurrentDateStart =
                STARTOFYEAR ( CurrentDateEnd )

            VAR CurrentDates =
                DATESBETWEEN (
                    {{DATE}},
                    CurrentDateStart,
                    CurrentDateEnd
                )

            VAR Result =
                CALCULATE (
                    {{MEASURE}},
                    CurrentDates
                )

            RETURN
                Result
            """),
        new(48, new("PYTD", "Year", "PYTD", "PYTD：当前所选日期对应上一年累计的值。", []),
            """
            VAR CurrentDateEnd =
                LASTDATE ( {{DATE}} )

            VAR CurrentDateStart =
                STARTOFYEAR ( CurrentDateEnd )

            VAR PreviousDateStart =
                DATEADD (
                    CurrentDateStart,
                    -1,
                    YEAR
                )

            VAR PreviousDateEnd =
                DATEADD (
                    CurrentDateEnd,
                    -1,
                    YEAR
                )

            VAR PreviousDates =
                DATESBETWEEN (
                    {{DATE}},
                    PreviousDateStart,
                    PreviousDateEnd
                )

            VAR Result =
                IF (
                    PreviousDateStart = BLANK () || PreviousDateEnd = BLANK (),
                    BLANK (),
                    CALCULATE (
                        {{MEASURE}},
                        PreviousDates
                    )
                )

            RETURN
                Result
            """),
        new(49, new("ΔPYTD", "Year", "ΔPYTD", "ΔPYTD = YTD - PYTD。", ["YTD", "PYTD"]),
            """
            VAR CurrentDateEnd =
                LASTDATE ( {{DATE}} )

            VAR CurrentDateStart =
                STARTOFYEAR ( CurrentDateEnd )

            VAR PreviousDateStart =
                DATEADD (
                    CurrentDateStart,
                    -1,
                    YEAR
                )

            VAR PreviousDateEnd =
                DATEADD (
                    CurrentDateEnd,
                    -1,
                    YEAR
                )

            VAR CurrentDates =
                DATESBETWEEN (
                    {{DATE}},
                    CurrentDateStart,
                    CurrentDateEnd
                )

            VAR PreviousDates =
                DATESBETWEEN (
                    {{DATE}},
                    PreviousDateStart,
                    PreviousDateEnd
                )

            VAR YearToDateValue =
                CALCULATE (
                    {{MEASURE}},
                    CurrentDates
                )

            VAR PreviousYearToDateValue =
                IF (
                    PreviousDateStart = BLANK () || PreviousDateEnd = BLANK (),
                    BLANK (),
                    CALCULATE (
                        {{MEASURE}},
                        PreviousDates
                    )
                )

            VAR Result =
                YearToDateValue - PreviousYearToDateValue

            RETURN
                Result
            """),
        new(50, new("ΔPYTD%", "Year", "ΔPYTD%", "ΔPYTD%=(YTD-PYTD)/PYTD*100%。", ["YTD", "PYTD"]),
            """
            VAR CurrentDateEnd =
                LASTDATE ( {{DATE}} )

            VAR CurrentDateStart =
                STARTOFYEAR ( CurrentDateEnd )

            VAR PreviousDateStart =
                DATEADD (
                    CurrentDateStart,
                    -1,
                    YEAR
                )

            VAR PreviousDateEnd =
                DATEADD (
                    CurrentDateEnd,
                    -1,
                    YEAR
                )

            VAR CurrentDates =
                DATESBETWEEN (
                    {{DATE}},
                    CurrentDateStart,
                    CurrentDateEnd
                )

            VAR PreviousDates =
                DATESBETWEEN (
                    {{DATE}},
                    PreviousDateStart,
                    PreviousDateEnd
                )

            VAR YearToDateValue =
                CALCULATE (
                    {{MEASURE}},
                    CurrentDates
                )

            VAR PreviousYearToDateValue =
                IF (
                    PreviousDateStart = BLANK () || PreviousDateEnd = BLANK (),
                    BLANK (),
                    CALCULATE (
                        {{MEASURE}},
                        PreviousDates
                    )
                )

            VAR Result =
                DIVIDE (
                    YearToDateValue - PreviousYearToDateValue,
                    PreviousYearToDateValue,
                    BLANK ()
                )

            RETURN
                Result
            """),
    ];
}
