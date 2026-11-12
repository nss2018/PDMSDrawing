openfile /D:\pdmsAgent\bridge\result.txt write !F1
handle ANY
  openfile /D:\pdmsAgent\bridge\result.txt overwrite !F1
endhandle
writefile $!F1 |===CHUTU-START AGT78179257/S1/V1===|
-- 进 DRAW 模块(出图库); 已在 DRAW 则吞错
DRAW
handle ANY
endhandle
CE /*
-- registry 不存在才建(修复 server.js 原 if/else 反向 bug)
!F2 = false
CE /AGT_R78179257
handle ANY
  !F2 = true
endhandle
if (!F2) then
  NEW REGI /AGT_R78179257
  handle ANY
  endhandle
endif
NEW DRWG /AGT78179257
handle ANY
endhandle
NEW SHEE /AGT78179257/S1
handle ANY
endhandle
NEW VIEW /AGT78179257/S1/V1
handle ANY
endhandle
VSCA  0.02
VTYP  Universal
SIZE  1189 841
XYPS  X500 Y400
DIR   D
END
END
NEW LIBY /AGT78179257/LIBY
handle ANY
endhandle
NEW DLLB /AGT78179257/DRAWLIST
handle ANY
endhandle
NEW IDLI /AGT78179257/DRAWLIST/DRWG
handle ANY
endhandle
END
END
NEW TRLB /AGT78179257/TAGRULE
handle ANY
endhandle
END
END
END
END
CE /AGT78179257/S1/V1
!T1 = READFAIL
!T1 = !!ce.type
handle ANY
endhandle
!R1 = READFAIL
!R1 = !!ce.vsca
handle ANY
endhandle
!S1 = READFAIL
!S1 = !!ce.vtyp
handle ANY
endhandle
!B1 = READFAIL
!B1 = !!ce.dbType
handle ANY
endhandle
writefile $!F1 |VIEW_TYPE_$!T1|
writefile $!F1 |VIEW_VSCA_$!R1|
writefile $!F1 |VIEW_VTYPE_$!S1|
writefile $!F1 |VIEW_DB_$!B1|
writefile $!F1 |===CHUTU-OK===|
closefile $!F1