#!/bin/bash

# Simple backup script
DATE=$(date +%Y%m%d_%H%M%S)
BACKUP_DIR="/backup"
SOURCE_DIR="/data"

echo "Starting backup at $(date)"

# Create backup directory if it doesn't exist
mkdir -p $BACKUP_DIR

# Create tar archive
tar -czf "$BACKUP_DIR/backup_$DATE.tar.gz" -C $SOURCE_DIR .

echo "Backup completed at $(date)"
echo "Backup file: backup_$DATE.tar.gz"