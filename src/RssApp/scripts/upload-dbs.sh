#!/bin/zsh

# Define remote host
REMOTE_HOST="brandonchastain@4.155.24.155"
SSH_KEY="~/.ssh/id_rsa"

ssh -i ~/.ssh/id_rsa  -o StrictHostKeyChecking=no brandonchastain@4.155.24.155 '
        sudo systemctl stop rssapp
    '

# upload database files
scp -i $SSH_KEY -o StrictHostKeyChecking=no ./data/'C:\home\data\storage.db' $REMOTE_HOST:/home/brandonchastain/app/'C:\\home\\data\\storage.db'
scp -i $SSH_KEY -o StrictHostKeyChecking=no ./data/'C:\home\data\auth.db' $REMOTE_HOST:/home/brandonchastain/app/'C:\\home\\data\\auth.db'

ssh -i ~/.ssh/id_rsa  -o StrictHostKeyChecking=no brandonchastain@4.155.24.155 '
        sudo systemctl start rssapp
        sudo journalctl -u rssapp -f
        exit
    '