param([string]$InputPath='artifacts/avalonia-frame-trace.json', [string]$OutputPath='artifacts/frame-trace-analysis.json')
$d=Get-Content -LiteralPath $InputPath -Raw | ConvertFrom-Json
$events=@($d.frameTrace | Sort-Object Timestamp)
$factor=1000.0/$d.timestampFrequency
$samples=@{}
$starts=@{}
$activeDraw=@{}
$previousRaf=$null
$longIntervals=[Collections.Generic.List[object]]::new()
function AddSample([string]$name,[double]$value) {
    if(!$samples.ContainsKey($name)) {$samples[$name]=[Collections.Generic.List[double]]::new()}
    $samples[$name].Add($value)
}
foreach($e in $events) {
    $op=[string]$e.Operation
    switch($e.Stage) {
        'raf-enter' {
            if($null -ne $previousRaf) {
                $gap=($e.Timestamp-$previousRaf.Timestamp)*$factor
                AddSample 'rafIntervalMs' $gap
                if($gap -gt 25) {$longIntervals.Add(@{start=$previousRaf.Timestamp;end=$e.Timestamp;intervalMs=$gap;operation=$previousRaf.Operation})}
            }
            $previousRaf=$e
            $starts['raf-'+$op]=$e
        }
        'raf-exit' {if($starts.ContainsKey('raf-'+$op)){AddSample 'rafWorkMs' (($e.Timestamp-$starts['raf-'+$op].Timestamp)*$factor)}}
        'control-render-start' {$starts['control-'+$op]=$e}
        'continuation-queued' {$starts['queue-'+$op]=$e}
        'continuation-run' {AddSample 'continuationDelayMs' (($e.Timestamp-$starts['queue-'+$op].Timestamp)*$factor)}
        'custom-draw-start' {
            $starts['custom-'+$op]=$e
            AddSample 'controlToCustomMs' (($e.Timestamp-$starts['control-'+$op].Timestamp)*$factor)
        }
        'drawframe-call' {$activeDraw[[string]$e.Thread]=$e; $starts['call-'+$op]=$e}
        'geometry-start' {
            $call=$activeDraw[[string]$e.Thread]
            AddSample 'drawCallToFirstGeometryMs' (($e.Timestamp-$call.Timestamp)*$factor)
            $starts['geometry-'+$call.Operation]=$e
        }
        'geometry-end' {
            $call=$activeDraw[[string]$e.Thread]
            AddSample 'geometryWorkMs' (($e.Timestamp-$starts['geometry-'+$call.Operation].Timestamp)*$factor)
        }
        'custom-draw-end' {AddSample 'customDrawTotalMs' (($e.Timestamp-$starts['custom-'+$op].Timestamp)*$factor)}
    }
}
$stats=@{}
foreach($name in $samples.Keys) {
    $values=@($samples[$name] | Sort-Object)
    $stats[$name]=@{count=$values.Count;mean=($values | Measure-Object -Average).Average;p50=$values[[math]::Ceiling(($values.Count-1)*.5)];p95=$values[[math]::Ceiling(($values.Count-1)*.95)];max=$values[-1]}
}
$examples=@()
foreach($interval in @($longIntervals | Sort-Object intervalMs -Descending | Select-Object -First 5)) {
    $examples+=@{intervalMs=$interval.intervalMs;events=@($events | Where-Object {$_.Timestamp -ge $interval.start -and $_.Timestamp -le $interval.end} | ForEach-Object {@{stage=$_.Stage;op=$_.Operation;thread=$_.Thread;offsetMs=($_.Timestamp-$interval.start)*$factor;consumed=$_.ConsumedBatch;measured=$_.MeasuredBatch}})}
}
$report=@{kind='diagnostic-frame-trace-analysis';source=$InputPath;warning='Draw-call to first-geometry includes lock wait, descheduling and initial drawing setup; not a pure lock measurement. Instrumented run, not acceptance FPS.';threads=@($events | Group-Object Thread | Select-Object Name,Count);statistics=$stats;longRafIntervalCount=$longIntervals.Count;longIntervalExamples=$examples}
$report | ConvertTo-Json -Depth 10 | Set-Content -LiteralPath $OutputPath
$stats.GetEnumerator() | ForEach-Object {[pscustomobject]@{phase=$_.Key;mean=$_.Value.mean;p50=$_.Value.p50;p95=$_.Value.p95;max=$_.Value.max}} | Sort-Object phase | Format-Table
"RAF intervals >25ms: $($longIntervals.Count)/$($samples['rafIntervalMs'].Count)"
