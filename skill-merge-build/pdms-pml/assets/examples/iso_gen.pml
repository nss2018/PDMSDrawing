-- ISO v10: world enumeration + CE-fallback types, first PIPE -> ISODRAFTMODE + options + DETAIL + readback
!MODX = 'UNSET'
!MODX = !!module().appkey
handle ANY
endhandle
openfile /D:\pdmsAgent\iso\results\iso_e01.txt write !EV1
writefile $!EV1 |===ISO_START===|
writefile $!EV1 |module=$!MODX|
closefile $!EV1
!pc = 0
!P1NAME = 'NONE'
!P1REF = 'NONE'
CE /*
var !W1 coll all for CE
handle ANY
endhandle
openfile /D:\pdmsAgent\iso\results\iso_e02.txt write !EV2
!c1 = 0
do !i1 from 1 to 200
!M1 = !W1[!i1]
handle ANY
break
endhandle
!c1 = !c1 + 1
!MS1 = 'REFFAIL'
!MS1 = !M1.string()
handle ANY
endhandle
CE $!MS1
handle ANY
endhandle
!T1 = 'UNK'
!T1 = !!ce.type
handle ANY
endhandle
!nm1 = 'UNK'
!nm1 = !!ce.name
handle ANY
endhandle
if (!T1 eq 'PIPE') then
writefile $!EV2 |L1 #=$!c1 ref=$!MS1 type=$!T1 name=$!nm1|
!pc = !pc + 1
if (!P1NAME eq 'NONE') then
!P1NAME = !nm1
!P1REF = !MS1
endif
endif
enddo
writefile $!EV2 |L1_COUNT=$!c1 PIPES=$!pc FIRST=$!P1NAME|
closefile $!EV2
$P ISO: world scan done members=$!c1 pipes=$!pc first=$!P1NAME
if (!pc gt 0) then
CE $!P1NAME
handle ANY
$P ISO: CE pipe failed
endhandle
!CNAME = 'CHKFAIL'
!CNAME = !!ce.name
handle ANY
endhandle
!CTYPE = 'UNK'
!CTYPE = !!ce.type
handle ANY
endhandle
$P ISO: gen start pipe=$!CNAME type=$!CTYPE
ISODRAFTMODE
handle ANY
$P ISO: ISODRAFTMODE failed
endhandle
options default
!OPTFILE = '%PMLLIB%/Common_Tools/previewisostddxf.txt'
$M "$!OPTFILE"
handle ANY
$P ISO: options load failed
endhandle
$P ISO: options ok
DETAIL $!CTYPE $!CNAME
handle ANY
$P ISO: DETAIL failed
endhandle
$P ISO: DETAIL done
var !STATUS isodraw status
handle ANY
!STATUS = 'UNSET'
endhandle
var !NPL isodraw number
handle ANY
!NPL = '0'
endhandle
openfile /D:\pdmsAgent\iso\results\iso_e03.txt write !EV3
writefile $!EV3 |PIPE=$!CNAME type=$!CTYPE|
writefile $!EV3 |STATUS=$!STATUS|
writefile $!EV3 |NPLOTS=$!NPL|
do !p from 1 to 3
var !PF isodraw plotfile $!p filename
handle ANY
!PF = 'ERR'
endhandle
var !DT isodraw plotfile $!p drawing 1 title
handle ANY
!DT = 'ERR'
endhandle
var !DS isodraw plotfile $!p drawing 1 sheet
handle ANY
!DS = 'ERR'
endhandle
writefile $!EV3 |PLOT$!p file=$!PF title=$!DT sheet=$!DS|
enddo
closefile $!EV3
$P ISO: status $!STATUS plots=$!NPL
EXIT
handle ANY
$P ISO: EXIT failed
endhandle
$P ISO: back to design
endif
openfile /D:\pdmsAgent\iso\results\iso_e04.txt write !EV4
writefile $!EV4 |GEN COMPLETE|
writefile $!EV4 |===ISO_END===|
closefile $!EV4
$P ISO: TTY gen complete
QUIT
QUIT
