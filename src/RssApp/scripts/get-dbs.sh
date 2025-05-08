#!/bin/zsh

# Define remote host
REMOTE_HOST="brandonchastain@4.155.24.155"
SSH_KEY="~/.ssh/id_rsa"

# Create local data directory if it doesn't exist
mkdir -p ./data

# Download database files
scp -i $SSH_KEY -o StrictHostKeyChecking=no $REMOTE_HOST:/home/brandonchastain/app/'C:\\home\\data\\storage.db' ./data/
scp -i $SSH_KEY -o StrictHostKeyChecking=no $REMOTE_HOST:/home/brandonchastain/app/'C:\\home\\data\\auth.db' ./data/
