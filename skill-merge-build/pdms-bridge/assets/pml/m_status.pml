openfile /D:\pdmsAgent\bridge\result.txt write !F1
handle ANY
  openfile /D:\pdmsAgent\bridge\result.txt overwrite !F1
endhandle
!M1 = ERR
handle ANY
  !M1 = !!ce.dbType
endhandle
writefile $!F1 |DBTYPE_$!M1|
!M2 = ERR
handle ANY
  !M2 = !!mdb.current.name
endhandle
writefile $!F1 |MDB_$!M2|
!M3 = ERR
handle ANY
  !M3 = !!ce.type
endhandle
writefile $!F1 |CETYPE_$!M3|
closefile $!F1