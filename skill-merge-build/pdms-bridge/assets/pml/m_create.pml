openfile /D:\pdmsAgent\bridge\result.txt write !F1
handle ANY
  openfile /D:\pdmsAgent\bridge\result.txt overwrite !F1
endhandle
writefile $!F1 |===CHUTU-START AGTDRAW_1791976415257/S1/V1===|
!F2 = false
CE /AGTREG_1791976415257
handle ANY
  !F2 = true
endhandle
if (!F2) then
  NEW REGI /AGTREG_1791976415257
endif
NEW DRWG /AGTDRAW_1791976415257
NEW SHEE /AGTDRAW_1791976415257/S1
NEW VIEW /AGTDRAW_1791976415257/S1/V1
VSCA  0.02
VTYP  Universal
SIZE  1189 841
XYPS  X500 Y400
DIR   D
END
END
NEW LIBY /AGTDRAW_1791976415257/LIBY
NEW DLLB /AGTDRAW_1791976415257/DRAWLIST
NEW IDLI /AGTDRAW_1791976415257/DRAWLIST/DRWG
END
END
NEW TRLB /AGTDRAW_1791976415257/TAGRULE
END
END
END
END
CE /AGTDRAW_1791976415257/S1/V1
!T1 = READFAIL
handle ANY
  !T1 = !!ce.type
endhandle
writefile $!F1 |VIEW_TYPE_$!T1|
!R1 = READFAIL
handle ANY
  !R1 = !!ce.vsca
endhandle
writefile $!F1 |VIEW_VSCA_$!R1|
!S1 = READFAIL
handle ANY
  !S1 = !!ce.vtyp
endhandle
writefile $!F1 |VIEW_VTYPE_$!S1|
!B1 = READFAIL
handle ANY
  !B1 = !!ce.dbType
endhandle
writefile $!F1 |VIEW_DB_$!B1|
writefile $!F1 |===CHUTU-OK===|
closefile $!F1