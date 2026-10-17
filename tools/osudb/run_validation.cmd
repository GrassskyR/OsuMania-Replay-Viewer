@echo off
REM Re-run every validation and write the raw logs used in the format docs.
REM Usage:  tools\osudb\run_validation.cmd
setlocal
set PY=C:\Python311\python.exe
set HERE=%~dp0
set OUT=%HERE%out
if not exist "%OUT%" mkdir "%OUT%"

echo === scores.db === > "%OUT%\scoresdb_validation.log"
"%PY%" "%HERE%scoresdb.py" --validate --osr-sample 300 --json "%OUT%\scoresdb_validation.json" >> "%OUT%\scoresdb_validation.log" 2>&1

echo === osu!.db === > "%OUT%\beatmapdb_validation.log"
"%PY%" "%HERE%beatmapdb.py" --validate --sample 500 --summary-json "%OUT%\beatmapdb_validation.json" >> "%OUT%\beatmapdb_validation.log" 2>&1

echo === scores.db (FULL population) === > "%OUT%\scoresdb_validation_full.log"
"%PY%" "%HERE%scoresdb.py" --validate --osr-sample 5420 --json "%OUT%\scoresdb_validation_full.json" >> "%OUT%\scoresdb_validation_full.log" 2>&1

echo === osu!.db (FULL population) === > "%OUT%\beatmapdb_validation_full.log"
"%PY%" "%HERE%beatmapdb.py" --validate --sample 25000 --summary-json "%OUT%\beatmapdb_validation_full.json" >> "%OUT%\beatmapdb_validation_full.log" 2>&1

echo === string encoding === > "%OUT%\encoding_report.txt"
"%PY%" "%HERE%probe_encoding_18.py" >> "%OUT%\encoding_report.txt" 2>&1
"%PY%" "%HERE%probe_encoding_17.py" >> "%OUT%\encoding_report.txt" 2>&1
echo === string encoding (probe 17 report) === > "%OUT%\encoding_report_17.txt"
type "%OUT%\encoding_report.txt" >> "%OUT%\encoding_report_17.txt"

echo === sample indexes === >> "%OUT%\scoresdb_validation.log"
"%PY%" "%HERE%scoresdb.py" --json "%OUT%\scoresdb_sample.json" --json-groups 25 >> "%OUT%\scoresdb_validation.log" 2>&1
"%PY%" "%HERE%beatmapdb.py" --json "%OUT%\beatmapdb_sample.json" --json-count 100 >> "%OUT%\beatmapdb_validation.log" 2>&1

"%PY%" "%HERE%probe_anomalies_16.py" >> "%OUT%\anomalies.log" 2>&1
"%PY%" "%HERE%probe_beatmapdb_20.py" >> "%OUT%\anomalies.log" 2>&1

echo === spec sanity + committed sample === >> "%OUT%\scoresdb_validation.log"
"%PY%" "%HERE%speccheck.py" >> "%OUT%\scoresdb_validation.log" 2>&1
"%PY%" "%HERE%emit_samples.py" >> "%OUT%\scoresdb_validation.log" 2>&1
echo done. logs in "%OUT%"
endlocal
