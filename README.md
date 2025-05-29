레지스트리 등록 방법

1. 윈도우키 + R 실행 후 regedit 입력
2. HKEY_LOCAL_MACHINE\SOFTWARE\Classes\.enc\shell\open\command 경로에 맞게 키 생성 (open , edit 키 둘다 생성)
3. .enc키의 기본값이 encfile로 되어 있는지 확인
4. assoc 명령어로 등록 확인 assoc .enc # 결과: .enc=encfile
5. 실행하고자 하는 exe파일의 경로를 찾아 등록
6. ftype encfile="C:\경로\ConsoleApp1.exe" "%1"
7. ftype encfile 명령어를 입력하여 경로가 제대로 등록 됐는지 확인
8. enc파일을 클릭하여 실행이 되는지 확인
