openfile /D:\pdmsAgent\bridge\result.txt write !F1
handle ANY
  openfile /D:\pdmsAgent\bridge\result.txt overwrite !F1
endhandle
!F2=false
CE /AGT78037938/S1/V1
handle ANY
  !F2=true
endhandle
if (!F2) then
  writefile $!F1 |VIEW_NOT_FOUND|
else
!T1=READFAIL
!T1=!!ce.type
handle ANY
endhandle
!R1=READFAIL
!R1=!!ce.vsca
handle ANY
endhandle
!S1=READFAIL
!S1=!!ce.vtyp
handle ANY
endhandle
!Z1=READFAIL
!Z1=!!ce.size
handle ANY
endhandle
writefile $!F1 |VIEW_TYPE_$!T1|
writefile $!F1 |VIEW_VSCA_$!R1|
writefile $!F1 |VIEW_VTYPE_$!S1|
writefile $!F1 |VIEW_SIZE_$!Z1|
endif
writefile $!F1 |===VERIFY-END===|
closefile $!F1