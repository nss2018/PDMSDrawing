openfile /D:\pdmsAgent\bridge\result.txt write !F1
handle ANY
  openfile /D:\pdmsAgent\bridge\result.txt overwrite !F1
endhandle
!F2 = false
CE /AGTDRAW_1791976415257/S1/V1
handle ANY
  !F2 = true
endhandle
if (!F2) then
  writefile $!F1 |VIEW_NOT_FOUND|
else
  var !L1 coll all for ce
handle ANY
  var !L1 ARRAY()
endhandle
  !C1 = !L1.size()
writefile $!F1 |MEMBERS_$!C1|
  do !i indices !L1
    !M1 = !L1[!i]
    !MS1 = 'ERRSTR'
    !MS1 = !M1.string()
    handle ANY
    endhandle
    writefile $!F1 |DM_$!MS1|
  enddo
endif
writefile $!F1 |===LIST-END===|
closefile $!F1