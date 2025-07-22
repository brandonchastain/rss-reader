#!/bin/zsh

# az vm delete \
#   --resource-group rss-reader-linux-group \
#   --name rss-linux-vm \
#   --yes

# Create Azure VM if it doesn't exist
# az group create \
#   --name rss-reader-linux-group \
#   --location westus2

# az vm create \
#   --resource-group rss-reader-linux-group \
#   --name rss-linux-vm \
#   --image Ubuntu2204 \
#   --admin-username brandonchastain \
#   --generate-ssh-keys \
#   --size Standard_B1s \
#   --public-ip-sku Standard

# az network nsg rule delete \
#     --resource-group rss-reader-linux-group \
#     --nsg-name rss-linux-vmNSG \
#     --name open-port-80

# az network nsg rule delete \
#     --resource-group rss-reader-linux-group \
#     --nsg-name rss-linux-vmNSG \
#     --name open-port-443

# # Open port 80 for HTTP traffic
# az vm open-port \
#     --resource-group rss-reader-linux-group \
#     --name rss-linux-vm \
#     --port 443

# az vm open-port \
#     --resource-group rss-reader-linux-group \
#     --name rss-linux-vm \
#     --port 80

# Build and package
RELEASE_DEBUG="debug"
rm -f archive.zip
rm -rf bin/$RELEASE_DEBUG/net9.0/linux-x64/publish
dotnet publish -c $RELEASE_DEBUG -r linux-x64 --output bin/$RELEASE_DEBUG/net9.0/linux-x64/publish

pushd bin/$RELEASE_DEBUG/net9.0/linux-x64/publish/
zip -rv archive.zip ./*
popd

# Deploy

# Get VM's public IP
VM_IP=$(az vm show -d -g rss-reader-linux-group -n rss-linux-vm --query publicIps -o tsv)
SSH_KEY="$HOME/.ssh/id_rsa"

# Copy package to VM
scp -i "$SSH_KEY" -o StrictHostKeyChecking=no archive.zip brandonchastain@${VM_IP}:~/

# SSH into VM and deploy
ssh -i "$SSH_KEY" -o StrictHostKeyChecking=no brandonchastain@${VM_IP} '
    # Install dependencies
    sudo apt-get update
    sudo apt-get install -y unzip authbind

    # Configure authbind for port 80
    sudo touch /etc/authbind/byport/443
    sudo chmod 500 /etc/authbind/byport/443
    sudo chown brandonchastain /etc/authbind/byport/443
    # Install .NET 9.0 Runtime
    sudo snap install aspnetcore-runtime-90

    # Deploy app
    mkdir -p ~/app
    unzip -o ~/archive.zip -d ~/app
    cd ~/app
    chmod +x RssApp
    sudo systemctl stop rssapp || true
    sudo tee /etc/systemd/system/rssapp.service << EOF
[Unit]
Description=RSS Reader App
After=network.target

[Service]
WorkingDirectory=/home/brandonchastain/app
ExecStart=/usr/bin/authbind --deep /snap/aspnetcore-runtime-90/current/usr/bin//dotnet /home/brandonchastain/app/RssApp.dll --launch-profile https
User=brandonchastain
Environment=ASPNETCORE_URLS=https://+:443
Environment=ASPNETCORE_ENVIRONMENT=Production
Restart=always

[Install]
WantedBy=multi-user.target
EOF
    sudo systemctl daemon-reload
    sudo systemctl enable rssapp
    sudo systemctl start rssapp
'
