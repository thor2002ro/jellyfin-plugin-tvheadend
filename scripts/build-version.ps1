[CmdletBinding()]
param(
    [DateTimeOffset] $UtcTime = [DateTimeOffset]::UtcNow
)

$timezone = [TimeZoneInfo]::FindSystemTimeZoneById('Europe/Bucharest')
$localTime = [TimeZoneInfo]::ConvertTime($UtcTime, $timezone)
'{0}.{1}.{2}.{3:00}{4:00}' -f $localTime.Year, $localTime.Month, $localTime.Day, $localTime.Hour, $localTime.Minute
