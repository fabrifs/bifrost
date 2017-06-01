#!/bin/bash
apt-get update
apt-get install libnss3-tools

# Chrome
certutil -d sql:$HOME/.pki/nssdb -D -n MPOS 2> /dev/null
certutil -d sql:$HOME/.pki/nssdb -A -t "P,," -n MPOS -i $1/$2.crt

# Firefox
certutil -d $(find ~/.mozilla/ -name cert*.db | sed s/cert[0-9].db//g | sort | uniq) -D -n MPOS 2> /dev/null
certutil -d $(find ~/.mozilla/ -name cert*.db | sed s/cert[0-9].db//g | sort | uniq) -A -t "TCu,Cuw,Tuw" -n MPOS -i $1/$2.crt