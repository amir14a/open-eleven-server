#!/bin/sh
# Create one schema per title and grant the shared user full rights on each, so all
# four servers share one MySQL instance without ever sharing data.
set -e
for db in pes2010pc pes2011pc pes2012pc pes2013pc; do
  mysql -u root -p"$MYSQL_ROOT_PASSWORD" -e \
    "CREATE DATABASE IF NOT EXISTS \`$db\`; GRANT ALL PRIVILEGES ON \`$db\`.* TO '$MYSQL_USER'@'%';"
done
mysql -u root -p"$MYSQL_ROOT_PASSWORD" -e "FLUSH PRIVILEGES;"