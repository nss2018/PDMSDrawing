openfile /D:\pdmsAgent\bridge\result.txt write !F1
handle ANY
  openfile /D:\pdmsAgent\bridge\result.txt overwrite !F1
endhandle
writefile $!F1 |T3_START AGTDRAW_1791976609297b|
handle ANY
  NEW DRWG /AGTDRAW_1791976609297b
  NEW SHEE /AGTDRAW_1791976609297b/S1
  NEW VIEW /AGTDRAW_1791976609297b/S1/V1
  VSCA  0.02
  VTYP  Universal
  SIZE  1189 841
  XYPS  X500 Y400
  DIR   D
  END
  END
  writefile $!F1 |T3_VIEWOK|
endhandle
closefile $!F1