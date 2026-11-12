openfile /D:\pdmsAgent\bridge\result.txt write !F1
handle ANY
  openfile /D:\pdmsAgent\bridge\result.txt overwrite !F1
endhandle
writefile $!F1 |===CHUTU-START AGT78369263/S1/V1===|
DRAW
handle ANY
endhandle
!F2 = false
CE /AGT_R78369263
handle ANY
  !F2 = true
endhandle
if (!F2) then
  NEW REGI /AGT_R78369263
  handle ANY
  endhandle
endif
NEW DRWG /AGT78369263
handle ANY
endhandle
NEW SHEE /AGT78369263/S1
handle ANY
endhandle
NEW VIEW /AGT78369263/S1/V1
VSCA  0.02
VTYP  Universal
SIZE  1189 841
XYPS  X500 Y400
DIR   D
END
END
END
END
CE /AGT78369263/S1/V1
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
!D1=READFAIL
!D1=!!ce.dir
handle ANY
endhandle
writefile $!F1 |VIEW_TYPE_$!T1|
writefile $!F1 |VIEW_VSCA_$!R1|
writefile $!F1 |VIEW_VTYPE_$!S1|
writefile $!F1 |VIEW_SIZE_$!Z1|
writefile $!F1 |VIEW_DIR_$!D1|
writefile $!F1 |===CHUTU-OK===|
closefile $!F1