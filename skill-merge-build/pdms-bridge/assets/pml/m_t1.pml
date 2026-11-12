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
  !M2 = !!ce.type
endhandle
writefile $!F1 |CETYPE_$!M2|
writefile $!F1 |T1_DONE|
closefile $!F1