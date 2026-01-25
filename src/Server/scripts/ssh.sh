#!/bin/zsh

# # Open port 80 for HTTP traffic
az vm open-port \
    --resource-group rss-reader-linux-group \
    --name rss-linux-vm \
    --port 22 \
    --priority 1000

# ssh
ssh -i ~/.ssh/id_rsa  -o StrictHostKeyChecking=no brandonchastain@4.155.24.155

# sudo journalctl -u rssserver -f
# systemctl stop rssapp
# systemctl start rssapp
