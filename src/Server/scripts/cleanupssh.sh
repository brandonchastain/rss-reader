az network nsg rule delete \
    --resource-group rss-reader-linux-group \
    --nsg-name rss-linux-vmNSG \
    --name default-allow-ssh

az network nsg rule delete \
    --resource-group rss-reader-linux-group \
    --nsg-name rss-linux-vmNSG \
    --name open-port-22
